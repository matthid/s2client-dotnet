namespace Starcraft2

module Sc2SettingsFile =

    type Sc2SettingsFile = 
      { Executable : string option
        Port : int option
        RealTime : bool option
        Map : string option
        Timeout : System.TimeSpan option }
        static member Empty = 
          { Executable = None
            Port = None
            RealTime = None
            Map = None
            Timeout = None }

    let settingsFromUserDir () =
        let userFolder = System.Environment.GetFolderPath(System.Environment.SpecialFolder.MyDocuments)
        let file = System.IO.Path.Combine(userFolder, "StarCraft II", "ExecuteInfo.txt")
        if System.IO.File.Exists file then
            let properties =
                System.IO.File.ReadAllLines file
                |> Seq.filter (fun line -> not (System.String.IsNullOrWhiteSpace line) && line.Length >= 2 && not (line.StartsWith "#") && not (line.StartsWith " "))
                |> Seq.map (fun line ->
                    let idx = line.IndexOf("=")
                    let key = line.Substring(0, idx)
                    let value = line.Substring(idx+1)
                    // key and value may contain spaces but we skip single spaces around "="
                    let trimmedKey = if key.EndsWith " " then key.Substring(0, key.Length - 1) else key
                    let trimmedValue = if value.StartsWith " " then value.Substring 1 else value
                    trimmedKey, trimmedValue)
                |> Map.ofSeq
            let executable =
                properties.TryFind "executable"
            let port =
                properties.TryFind "port"
                |> Option.bind (fun s -> match System.Int32.TryParse s with | true, s -> Some s | _ -> None)
            let realtime =
                properties.TryFind "realtime"
                |> Option.bind (fun s -> match System.Int32.TryParse s with | true, s -> Some s | _ -> None)
                |> Option.map (fun i -> i <> 0)
            let map = properties.TryFind "map"
            let timeout =
                properties.TryFind "timeout"
                |> Option.bind (fun s -> match System.Int32.TryParse s with | true, s -> Some s | _ -> None)
                |> Option.map (float >> System.TimeSpan.FromMilliseconds)
            { Executable = executable
              Port = port
              RealTime = realtime
              Map = map
              Timeout = timeout }
        else Sc2SettingsFile.Empty          