// Learn more about F# at http://fsharp.org

open System

open Starcraft2
open SC2APIProtocol

[<EntryPoint>]
let main argv =
    let tok = new System.Threading.CancellationTokenSource()

    let userSettings = Sc2SettingsFile.settingsFromUserDir()

    let instanceSettings = Instance.StartSettings.OfUserSettings userSettings

    let instance() = Instance.start(instanceSettings) |> Async.RunSynchronously

    let participants =
        [ Sc2Game.Participant(instance(), Race.Terran, (fun _ -> []))
          Sc2Game.Computer(Race.Terran, Difficulty.Hard) ]
    
    let settings = 
        { Sc2Game.GameSettings.OfUserSettings userSettings with
             Map = @"Ladder2017Season1\AbyssalReefLE.SC2Map"
             Realtime = true }
    Sc2Game.runGame settings participants |> Async.RunSynchronously

    
    0 // return an integer exit code
