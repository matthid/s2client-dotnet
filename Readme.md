## StarCraft II Client API for .NET

A .NET (C#, F#, VB.Net) port of https://github.com/Blizzard/s2client-api into .Net "Standard" 2.0.

> Warning: This is work in progress and APIs might change.

## NuGet

Hopefully soon.

## Why

TBD. Probably to make something awesome in a sane language ;)

## Usage

Current a bot is a simple function `GameState -> IEnumerable<SC2APIProtocol.Action>` so for every state the bot decides to do a list of actions.
There probably will be higher level interfaces with some predefined events/states later.

The following example will spawn a instance of Starcraft 2 (`Runner.run(Instance.start(settings))`)
then setup the game:
 - The map to use
 - some configurations (for example if played in realtime or not)
 - The participants. 

So for example to play against a hard bot on `AbyssalReefLE.SC2Map` you can download and install the map according to https://github.com/Blizzard/s2client-proto#installing-map-and-replay-packs and then start the game via the following code:

```csharp
static void Main(string[] args)
{
    var userSettings = Sc2SettingsFile.settingsFromUserDir();

    var instanceSettings = Instance.StartSettings.OfUserSettings(userSettings);

    Func<Instance.Sc2Instance> createInstance =
        () => Runner.run(Instance.start(instanceSettings));

    var participants = new Sc2Game.Participant[] {
        Sc2Game.Participant.CreateParticipant(
            createInstance(), 
            Race.Terran, 
            (state => (IEnumerable<SC2APIProtocol.Action>)new SC2APIProtocol.Action[] {})),
        Sc2Game.Participant.CreateComputer(Race.Terran, Difficulty.Hard)
    };

    var gameSettings =
        Sc2Game.GameSettings.OfUserSettings(userSettings)
        .WithMap(@"Ladder2017Season1\AbyssalReefLE.SC2Map")
        .WithRealtime(true);

    // Runs the game to the end with the given bots / map and configuration
    Runner.run(Sc2Game.runGame(gameSettings, participants));
}
```

In F# it looks a bit nicer ;)

```fsharp
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

```


The empty bot function will ensure you can play manually. Obviously you want to implement that with something useful.

Further documentation (regarding the datastructures and custom  StarCraft II Builds)
 - https://github.com/Blizzard/s2client-proto/tree/master/s2clientprotocol
 - https://github.com/Blizzard/s2client-proto
 - Reference C++ Implementation https://github.com/Blizzard/s2client-api

If you use the library don't hesitate to let me know ;)

## Building

Requirements:
 - Fake 5 (install via [chocolatey](https://chocolatey.org/packages/fake) `choco install fake --pre` or unzip [and add to path](https://github.com/fsharp/FAKE/releases))
 - Install [Dotnet SDK 2](https://www.microsoft.com/net/download/core)


1. Build the C# proto files:
   - `fake run build.fsx`
2. Build/Package the project
   - `dotnet pack src/s2client-dotnet.sln -o C:\proj\sc2\s2client-dotnet\release`

   Now the `C:\proj\sc2\s2client-dotnet\release` directory contains the nuget packages.

3. Run the test projects
   - `dotnet run --project s2client-testCSharp/s2client-testCSharp.csproj`
   - `dotnet run --project s2client-test/s2client-test.csproj`

