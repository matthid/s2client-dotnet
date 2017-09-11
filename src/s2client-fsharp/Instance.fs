namespace Starcraft2

open SC2APIProtocol

// manage a starcraft instance
module Instance =

    type Sc2Instance =
        { Connection : ProtbufConnection.Sc2Connection; Process : System.Diagnostics.Process }
        member x.Disconnect (exitInstance:bool) =
            x.Connection.Disconnect(exitInstance)
            if (not(x.Process.WaitForExit(1000))) then
                x.Process.Kill()
    let internal getFreePort () =
        let l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0)
        l.Start()
        let port = (l.LocalEndpoint:?>System.Net.IPEndPoint).Port
        l.Stop()
        port
    
    type StartSettings =
      { Port : int option
        Timeout : System.TimeSpan option
        Executable : string option }
        static member OfUserSettings (s:Sc2SettingsFile.Sc2SettingsFile) =
          { Port = s.Port
            Executable = s.Executable
            Timeout = s.Timeout }
        member x.WithPort port = { x with Port = Some port }
        member x.WithNoPort () = { x with Port = None }
        member x.WithTimeout timeout = { x with Timeout = Some timeout }
        member x.WithNoTimeout () = { x with Timeout = None }
        member x.WithExecutable executable = { x with Executable = Some executable }
        member x.WithNoExecutable () = { x with Executable = None }

    let start (settings:StartSettings) = async {
        let userSettings = lazy Sc2SettingsFile.settingsFromUserDir()
        let tok = new System.Threading.CancellationTokenSource()
        let port =
            match settings.Port with
            | Some port -> port
            | None -> 
                match userSettings.Value.Port with
                | Some port -> port
                | None -> getFreePort()
        let address = "127.0.0.1"
        let timeout = defaultArg settings.Timeout (System.TimeSpan.FromMinutes 1.0)
        let executable = 
            match settings.Executable with
            | Some exec -> exec
            | None -> 
                match userSettings.Value.Executable with
                | Some exec -> exec
                | None -> failwithf "No executable specified."
        let sc2Dir =  executable |> System.IO.Path.GetDirectoryName |> System.IO.Path.GetDirectoryName |> System.IO.Path.GetDirectoryName
        let supportDir = System.IO.Path.Combine(sc2Dir, "Support64")
        let proc = System.Diagnostics.ProcessStartInfo(executable)
        // -dataVersion
        // -windowwidth
        // -windowheight
        // -windowx
        // -windowy
        proc.Arguments <- sprintf "-listen %s -port %d -displayMode 0" address port
        proc.WorkingDirectory <- supportDir
        printfn "Starting SC2 ... (%s)" proc.Arguments
        let processInstance = System.Diagnostics.Process.Start(proc)

        let watch = System.Diagnostics.Stopwatch.StartNew()
        let mutable connection = None
        let mutable lastError = null
        while connection.IsNone && watch.Elapsed < timeout do
            try
                let! con = ProtbufConnection.connect address port timeout tok.Token
                connection <- Some con
            with :? System.Net.WebSockets.WebSocketException as err ->
                lastError <- err
        match connection with
        | None -> return raise <| System.TimeoutException("Could not connect within the specified time", lastError)
        | Some connection ->            
            return { Connection = connection; Process = processInstance } }

    type Participant =
        | Participant of Race
        | Computer of Race * Difficulty
        | Observer
        member x.PlayerType =
            match x with
            | Participant _ -> PlayerType.Participant
            | Computer _ -> PlayerType.Computer
            | Observer -> PlayerType.Observer      

    let createGame (instance:Sc2Instance) mapName (participants:Participant list) realTime = async {
        let req = new RequestCreateGame()
        for player in participants do
            let playerSetup = new PlayerSetup()
            playerSetup.Type <- player.PlayerType
            match player with
            | Participant race ->
                playerSetup.Race <- race
            | Computer (race, difficulty) ->
                playerSetup.Race <- race
                playerSetup.Difficulty <- difficulty
            | Observer -> ()
            req.PlayerSetup.Add(playerSetup)
        req.Realtime <- realTime

        // map
        if System.IO.Path.GetExtension mapName <> ".SC2Map" then
            req.BattlenetMapName <- mapName
        else
            let localmap = new LocalMap()
            if System.IO.File.Exists mapName then
                localmap.MapPath <- mapName
            else
                let ourRelative = 
                    System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location)
                let path = System.IO.Path.Combine(ourRelative, mapName)
                if System.IO.File.Exists path then
                    localmap.MapPath <- path
                else
                    // probably relative to "game directory" or "Remotely saved maps directory"
                    localmap.MapPath <- mapName
            req.LocalMap <- localmap

        // create the game        
        let! status = ProtbufConnection.createGame req instance.Connection
        assert (status = Status.InitGame)
        return  ()
    }

    type ClientPort =
      { GamePort : int
        BasePort : int }
    type Ports =
      { SharedPort : int 
        ServerPorts : ClientPort
        ClientPorts : ClientPort list }
    let joinGame (instance:Sc2Instance) (useFeatureLayers:SpatialCameraSetup option) (useRender:SpatialCameraSetup option) (setup:Participant) ports = async {
        let req = new RequestJoinGame()
        match setup with
        | Participant (race)
        | Computer (race, _) ->
            req.Race <- race
        | _ -> ()        
        
        // setup ports
        ports
        |> Option.iter (fun ports ->
            req.SharedPort <- ports.SharedPort
            let server_ports = new PortSet()
            server_ports.GamePort <- ports.ServerPorts.GamePort
            server_ports.BasePort <- ports.ServerPorts.BasePort
            for clientPorts in ports.ClientPorts do
                let cl = new PortSet()
                cl.BasePort <- clientPorts.BasePort
                cl.GamePort <- clientPorts.GamePort
                req.ClientPorts.Add(cl)
        )

        // interface
        let interfaceOpts = new InterfaceOptions()
        interfaceOpts.Raw <- true
        interfaceOpts.Score <- true
        useFeatureLayers
        |> Option.iter (fun featureLayer -> interfaceOpts.FeatureLayer <- featureLayer)
        useRender
        |> Option.iter (fun render -> interfaceOpts.Render <- render)
        req.Options <- interfaceOpts

        // Do the join command
        let! playerId, status = ProtbufConnection.joinGame req instance.Connection
        assert (status = Status.InGame)

        return playerId
    }
    let getObservation disableFog (instance:Sc2Instance) = async {
        // Do the join command
        let! responseObs, status = ProtbufConnection.getObservation disableFog instance.Connection
        assert (status = Status.InGame)
        return responseObs }

    let doStep stepSize (instance:Sc2Instance) = async {
        // Do the join command
        let! status = ProtbufConnection.doStep stepSize instance.Connection
        assert (status = Status.InGame)
        return () }

    let doActions actions (instance:Sc2Instance) = async {
        // Send Actions
        let! result, status = ProtbufConnection.doActions actions instance.Connection
        assert (status = Status.InGame)
        return result }