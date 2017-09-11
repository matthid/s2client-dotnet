namespace Starcraft2

open SC2APIProtocol


type GameState =
  { LastObservation : SC2APIProtocol.ResponseObservation option
    LastActions : SC2APIProtocol.Action list
    NewObservation : SC2APIProtocol.ResponseObservation
    // more global state
    PlayerId : PlayerId
     }
    static member Empty playerId = 
      { LastObservation = None
        LastActions = []
        NewObservation = null
        PlayerId = playerId }

type Sc2Bot = GameState -> SC2APIProtocol.Action list
type Sc2Observer = GameState -> unit

module Sc2Game =

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
        let firstInstance =
            participants
            |> Seq.tryPick (function 
                | Participant(instance,_,_) -> Some instance
                | Observer(instance,_) -> Some instance
                | _ -> None)
        let firstInstance =            
            match firstInstance with
            | None -> failwithf "At least one non-computer participant needs to be added!"
            | Some s -> s

        
        let simpleParticipants = participants |> List.map (fun p -> p.Simple)
        do! Instance.createGame firstInstance gameSettings.Map simpleParticipants gameSettings.Realtime

        // Join other instances
        let agents = participants |> Seq.sumBy (function Computer _ -> 0 | _ -> 1)
        let ports =
            if agents > 1 then
                let clientPortsRequired =
                    // one is the server
                    agents - 1 
                let shared = Instance.getFreePort()
                let server =  { Instance.ClientPort.BasePort = Instance.getFreePort();  Instance.ClientPort.GamePort = Instance.getFreePort() }
                let clients =
                    List.init clientPortsRequired (fun _ -> { Instance.ClientPort.BasePort = Instance.getFreePort();  Instance.ClientPort.GamePort = Instance.getFreePort() } )

                { Instance.SharedPort = shared
                  Instance.ServerPorts = server
                  Instance.ClientPorts = clients }
                |> Some
            else None                                  

        let playerIdTasks =
            participants
            |> List.map (fun part ->
                match part with
                | Participant (instance, _, _)
                | Observer (instance, _) ->
                   Instance.joinGame instance gameSettings.UseFeatureLayers gameSettings.UseRender part.Simple ports
                   |> Async.StartAsTask
                   |> Some
                | _ -> None)              
        for playerIdTask in playerIdTasks do
            match playerIdTask with
            | Some t -> do! t |> Async.AwaitTask |> Async.Ignore
            | None -> ()

        let playerIds =
            playerIdTasks |> List.map (Option.map (fun pit -> pit.Result))

        return playerIds
    }


    let runGame (gameSettings:GameSettings) (participants: Participant seq) = async {
        let participants = participants |> Seq.toList
        let! playerIds = setupAndConnect gameSettings participants

        let merged =
            List.zip participants playerIds
        let state = System.Collections.Concurrent.ConcurrentDictionary<_,GameState>()
        let getState playerId =
            state.GetOrAdd(playerId, fun _ -> GameState.Empty playerId)
        let updateState playerId newState =
            state.AddOrUpdate(playerId, newState, (fun _ _ -> newState))
            |> ignore

        let relevantPlayers =
            merged
            |> List.choose (fun (part, playerId) ->
                match part, playerId with
                | Participant (instance, _, bot), Some playerId ->
                    Some (playerId, instance, bot)
                | Observer (instance, bot), Some playerId ->
                    Some (playerId, instance, (fun data -> bot data; []))
                | Computer _, _ -> None
                | _ -> failwithf "Expected playerId when participant or observer but not when computer. %A" (part,playerId)
            )

        // Game loop
        while true do
            for (playerId, instance, bot) in relevantPlayers do
                let! obs = Instance.getObservation false instance
                // TODO: Higher level support, GetUnits -> Self -> StartLocation
                let lastState = getState playerId
                let state = 
                    { lastState with 
                        NewObservation = obs
                        LastObservation =
                            if not (isNull lastState.NewObservation) then Some lastState.NewObservation
                            else None }
                
                let actions = bot state
                if not gameSettings.Realtime then
                    do! Instance.doStep gameSettings.StepSize instance

                updateState playerId { state with LastActions = actions }

            // Execute actions
            for (playerId, instance, bot) in relevantPlayers do
                let lastState = getState playerId
                do! Instance.doActions lastState.LastActions instance |> Async.Ignore
        }