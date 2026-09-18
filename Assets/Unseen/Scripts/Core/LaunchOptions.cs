using Unseen.Net;

namespace Unseen.Core
{
    /// <summary>
    /// What the command line asked for.
    ///
    /// Split out of <see cref="UnseenBootstrap"/> so it can be tested. The parsing used to read
    /// Environment.GetCommandLineArgs() directly, which meant the only way to exercise it was to
    /// launch the process - so the arguments deciding whether a build is a server, a client or
    /// neither had no coverage at all, while being the first thing that runs and the most likely
    /// to be handed rubbish by a shortcut somebody edited.
    /// </summary>
    public struct LaunchOptions
    {
        public LaunchMode Mode;
        public int Seed;
        public int Entities;
        public int ListenPort;
        public NetEndpoint? ConnectTo;
        public string RequestedName;

        /// <summary>
        /// A worse network to pretend this machine is on, from `-netsim domestic` or
        /// `-netsim mobile`. Null means the real one, which is what a shipping build gets.
        /// </summary>
        public Net.NetworkConditions? Conditions;

        /// <summary>
        /// Seconds to hold the lobby once somebody is in it, from `-lobby`. Null keeps the default.
        ///
        /// Worth an option because the right answer depends on how players arrive. Matchmaking
        /// hands over a lobby of people who have already loaded; a bare server watches them trickle
        /// in over a minute of town generation, and a ten-second countdown started by the first
        /// arrival locks out everybody behind them.
        /// </summary>
        public float? LobbySeconds;

        /// <summary>What could not be read, or null when everything parsed.</summary>
        public string Error;

        /// <summary>Whether -entities was given, so a zero is not mistaken for a request.</summary>
        public bool HasEntities;

        /// <summary>Whether -seed was given.</summary>
        public bool HasSeed;

        /// <summary>
        /// Whether anything on the command line actually chose a mode.
        ///
        /// Without this a caller cannot tell "no switches were given" from "offline was asked for",
        /// and applying the default over the top would overwrite a mode set in the inspector or by
        /// a tool - the screenshot capture sets ListenServer before booting, and would have been
        /// quietly demoted to offline practice every run.
        /// </summary>
        public bool HasMode;

        /// <summary>
        /// Reads the switches, and never throws.
        ///
        /// Every value goes through TryParse and every lookahead is bounds checked, because a
        /// trailing switch is an ordinary thing to type. Throwing here happens before there is a
        /// log file to explain it: the player sees the process vanish and has nothing to report
        /// except that it closed.
        /// </summary>
        public static LaunchOptions Parse(string[] args)
        {
            var options = new LaunchOptions
            {
                Mode = LaunchMode.OfflinePractice,
                ListenPort = UnseenTransport.DefaultPort
            };

            if (args == null) return options;

            for (int i = 0; i < args.Length; i++)
            {
                string next = i + 1 < args.Length ? args[i + 1] : null;

                switch (args[i])
                {
                    case "-server":
                    case "--server":
                        options.Mode = LaunchMode.DedicatedServer;
                        options.HasMode = true;
                        break;

                    case "-listen":
                        options.Mode = LaunchMode.ListenServer;
                        options.HasMode = true;
                        break;

                    case "-seed":
                        if (int.TryParse(next, out int seed))
                        {
                            options.Seed = seed;
                            options.HasSeed = true;
                        }
                        break;

                    case "-entities":
                        if (int.TryParse(next, out int entities))
                        {
                            options.Entities = entities;
                            options.HasEntities = true;
                        }
                        break;

                    case "-port":
                        // Out of range is ignored rather than clamped: a port typed with an extra
                        // digit is a mistake, and quietly listening somewhere else is worse than
                        // listening where the default says.
                        if (int.TryParse(next, out int port) && port > 0 && port <= 65535)
                            options.ListenPort = port;
                        break;

                    case "-connect":
                        if (ConnectTarget.TryParse(next, out NetEndpoint target))
                        {
                            options.ConnectTo = target;

                            // Typing "-connect somewhere" and nothing else is what everybody does,
                            // so it means join that server. Demanding a second flag would produce a
                            // confusing single-player session for anyone who forgot it.
                            options.Mode = LaunchMode.Client;
                            options.HasMode = true;
                        }
                        else
                        {
                            options.Error = $"could not read -connect '{next}'. " +
                                            "Expected host:port, for example 192.168.1.50:7777.";
                        }
                        break;

                    case "-name":
                        if (!string.IsNullOrEmpty(next)) options.RequestedName = next;
                        break;

                    case "-lobby":
                        if (float.TryParse(next, out float lobby) && lobby >= 0f)
                            options.LobbySeconds = lobby;
                        break;

                    case "-netsim":
                        // Named conditions rather than five separate numeric flags. The useful
                        // question is "does this hold up on a normal home connection", not "what
                        // does 37 ms of jitter feel like", and a name is something you can ask a
                        // playtester to reproduce over the phone.
                        switch (next)
                        {
                            case "domestic":
                                options.Conditions = Net.NetworkConditions.Domestic;
                                break;

                            case "mobile":
                                options.Conditions = Net.NetworkConditions.Mobile;
                                break;

                            default:
                                // Refused rather than ignored. Silently playing on a perfect link
                                // is the one outcome that hides the flag having done nothing, and
                                // the whole point of asking for it was to stop trusting a perfect
                                // link.
                                options.Error = $"unknown -netsim '{next}'. Expected domestic or mobile.";
                                break;
                        }
                        break;
                }
            }

            return options;
        }
    }
}
