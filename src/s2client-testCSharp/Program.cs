using System;
using Starcraft2;
using SC2APIProtocol;
using System.Collections.Generic;

namespace s2client_testCSharp
{
    class Program
    {
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
    }
}
