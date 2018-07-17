namespace Starcraft2

open SC2APIProtocol

type GameState =
    { 
        LastObservation : SC2APIProtocol.ResponseObservation option
        LastActions : SC2APIProtocol.Action list
        NewObservation : SC2APIProtocol.ResponseObservation
        PlayerId : uint32
        GameInfo : SC2APIProtocol.ResponseGameInfo
    }

    static member InitialState playerId observation gameInfo =
        {
            LastObservation = None
            LastActions = []
            NewObservation = observation
            PlayerId = playerId
            GameInfo = gameInfo
        }

    member this.NextGameState lastActions observation =
        {this with
            LastObservation = this.NewObservation |> Some
            LastActions = lastActions
            NewObservation = observation
        }

type Sc2Bot = GameState -> SC2APIProtocol.Action list
type Sc2Observer = GameState -> unit

module Sc2Game =
    open Rail
    open Instance

    type Participant =
        | Participant of Instance.Sc2Instance * Race * Sc2Bot
        | Computer of Race * Difficulty
        | Observer of Instance.Sc2Instance * Sc2Observer
        member x.Simple =
          match x with
          | Participant (_, race, _) -> Instance.Participant race
          | Computer (race, difficulty) -> Instance.Computer(race, difficulty)
          | Observer _ -> Instance.Observer
        static member CreateParticipant (instance : Instance.Sc2Instance, race : Race, bot : System.Func<GameState, SC2APIProtocol.Action seq>) =
            Participant.Participant(instance, race, (fun data -> bot.Invoke(data) |> Seq.toList))
        static member CreateComputer(race, difficulty) = Participant.Computer(race, difficulty)
        static member CreateObserver(instance: Instance.Sc2Instance, observer : System.Action<GameState>) =
            Participant.Observer(instance, (fun data -> observer.Invoke(data)))

    type GameSettings = 
      { Realtime : bool
        FullScreen : bool
        StepSize : uint32
        UseFeatureLayers : SpatialCameraSetup option
        UseRender : SpatialCameraSetup option
        Map : string }
        static member OfUserSettings (s:Sc2SettingsFile.Sc2SettingsFile) =
          { Realtime = defaultArg s.RealTime false
            Map = defaultArg s.Map "AbyssalReefLE.SC2Map"
            StepSize = 1u
            UseFeatureLayers = None
            UseRender = None
            FullScreen = false }
        static member Empty = GameSettings.OfUserSettings (Sc2SettingsFile.Sc2SettingsFile.Empty)
        member x.WithMap map = { x with Map = map }
        member x.WithStepsize stepSize = { x with StepSize = stepSize }
        member x.WithRealtime realtime = { x with Realtime = realtime }
        member x.WithFeatureLayers featureLayers = { x with UseFeatureLayers = Some featureLayers }
        member x.WithNoFeatureLayers () = { x with UseFeatureLayers = None }
        member x.WithRender render = { x with UseRender = Some render }
        member x.WithNoRender () = { x with UseRender = None }
        member x.WithFullScreen fullscreen = { x with FullScreen = fullscreen }

    let private setupAndConnect (gameSettings:GameSettings) (participants: Participant list) = async {
        // Create game with first client
        let validateParticipants() =
            let firstInstance =
                participants
                |> Seq.tryPick (function 
                    | Participant(instance,_,_) -> Some instance
                    | Observer(instance,_) -> Some instance
                    | _ -> None)
                    
            match firstInstance with
            | None -> "At least one non-computer participant needs to be added!" |> ConfigError |> Error
            | Some s -> s |> Ok
            
        let simpleParticipants = participants |> List.map (fun p -> p.Simple)

        let createGame firstInstance =
            Instance.createGame firstInstance gameSettings.Map simpleParticipants gameSettings.Realtime

        let joinOtherInstances _ =
            let agents = participants |> Seq.sumBy (function Computer _ -> 0 | _ -> 1)

            if agents > 1 then
                let clientPortsRequired =
                    // one is the server
                    agents - 1 
                let shared = Instance.getFreePort()
                let server =  { Instance.ClientPort.BasePort = Instance.getFreePort();  Instance.ClientPort.GamePort = Instance.getFreePort() }
                let clients =
                    List.init clientPortsRequired (fun _ -> { Instance.ClientPort.BasePort = Instance.getFreePort();  Instance.ClientPort.GamePort = Instance.getFreePort() } )

                { 
                    Instance.SharedPort = shared
                    Instance.ServerPorts = server
                    Instance.ClientPorts = clients 
                }
                |> Some
            else None

        let getPlayerIds ports =
            participants
            |> List.map (fun part ->
                let attachPart x = part, x
                match part with
                |Participant (instance, _, _)
                |Observer (instance, _) ->
                    Instance.joinGame instance gameSettings.UseFeatureLayers gameSettings.UseRender part.Simple ports
                    |> Async.RunSynchronously
                    |> Result.map Some
                    |> Result.map attachPart
                |_ -> None |> attachPart |> Ok
            )
            |> Result.listFold

        return!
            validateParticipants()
            |> Result.bindAsyncBinder createGame
            |> Result.bindAsyncInput (joinOtherInstances >> Ok)
            |> Result.bindAsyncInput getPlayerIds
    }

    type private PlayerData =
        {
            PlayerId:uint32
            Instance:Sc2Instance
            Bot:Sc2Bot
        }
        static member Create playerId instance bot =
            {
                PlayerId = playerId
                Instance = instance
                Bot = bot
            }

    let runGame (gameSettings:GameSettings) (participants: Participant seq) = async {
        let getRelevantPlayers players =
            players
            |> List.choose (fun (part, playerId) -> 
                match part, playerId with
                |Participant (instance, _, bot), Some playerId ->
                    PlayerData.Create playerId instance bot |> Ok |> Some
                    //(playerId, instance, bot) :: state
                |Observer (instance, bot), Some playerId ->
                    PlayerData.Create playerId instance (fun data -> bot data; []) |> Ok |> Some
                |Computer _, _ -> None
                | _ -> sprintf "Expected playerId when participant or observer but not when computer. %A" (part,playerId) |> ConfigError |> Error |> Some
            ) |> Result.listFold

        // Get the static gameInfo
        let getStaticGameInfo =
            List.map (fun (player:PlayerData) -> 
                Instance.getGameInfo player.Instance
                |> Result.mapAsyncInput (fun gi -> player, gi)
            ) >> Async.Parallel >> Async.RunSynchronously >> List.ofArray >> Result.listFold
            
        let getInitialGameState =
            List.map (fun (player:PlayerData, gameInfo:ResponseGameInfo) ->
                Instance.getObservation false player.Instance
                |> Result.mapAsyncInput (fun obs -> player, GameState.InitialState player.PlayerId obs gameInfo)
            ) >> Async.Parallel >> Async.RunSynchronously >> List.ofArray >> Result.listFold

        let rec gameLoop playersResult =
            match playersResult with
            |Ok players ->
                players
                |> List.map (fun (player:PlayerData, gameState:GameState) ->
                    let getActions = Result.tryCatch player.Bot (fun _ -> BotError)

                    let executeActions actions =
                        Instance.doActions actions player.Instance //Travis: would this information (ActionResult) ever be useful to a bot? I see no reason against providing it as part of the game state
                        |> Result.mapAsyncInput (fun x -> actions)
                    

                    let doStep actions = async{
                        if not gameSettings.Realtime then
                            return! 
                                Instance.doStep gameSettings.StepSize player.Instance
                                |> Result.mapAsyncInput (fun _ -> actions)
                        else
                            return Ok actions
                    }
                    
                    let getNextGameState actions = 
                        Instance.getObservation false player.Instance
                        |> Result.mapAsyncInput (fun obs -> player, gameState.NextGameState actions obs)

                    gameState
                    |> getActions
                    |> Result.mapAsyncMapper executeActions
                    |> Result.mapAsync doStep
                    |> Result.mapAsync getNextGameState
                ) |> Async.Parallel |> Async.RunSynchronously |> List.ofArray |> Result.listFold |> gameLoop
            |Error er -> Error er

        let! gameLoopInputs =
            participants |> List.ofSeq
            |> setupAndConnect gameSettings 
            |> Result.bindAsyncInput getRelevantPlayers
            |> Result.bindAsyncInput getStaticGameInfo
            |> Result.bindAsyncInput getInitialGameState

        return gameLoopInputs |> gameLoop
    }