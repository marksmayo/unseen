using System;
using System.Collections.Generic;

namespace Unseen.Core
{
    /// <summary>How bad it is. Ordered, so a filter can be "this and worse".</summary>
    public enum LogLevel : byte
    {
        Info = 0,
        Warn = 1,
        Error = 2
    }

    /// <summary>
    /// What a dedicated server writes about itself while nobody is watching.
    ///
    /// A server log is read in two situations and both are unhappy: something went wrong in a match
    /// hours ago, or a soak has been running overnight and somebody wants to know whether it is
    /// still well. Both want the same things - a UTC timestamp, a level early enough to grep for,
    /// and a shape a script can read without guessing.
    ///
    /// Repetition is the part that is easy to leave out and expensive to leave out. One system
    /// throwing on every tick is sixty lines a second: three hundred thousand in an hour, a full
    /// disk by morning, and the one interesting line - the first - buried a quarter of a million
    /// lines up. Saying it once and counting the rest keeps both the disk and the signal.
    ///
    /// Writes to stdout by default, because that is what a container expects and it makes retention
    /// somebody else's problem, correctly: a process that rotates its own files in a cluster is
    /// duplicating what the cluster already does, and doing it worse.
    /// </summary>
    public sealed class ServerLog
    {
        /// <summary>
        /// How long the same message is folded together before being said again.
        ///
        /// Long enough to swallow a burst, short enough that a condition which persists is still
        /// visibly persisting. A failure that has been happening for an hour should appear in the
        /// log more than once, or somebody scrolling to the end will think it stopped.
        /// </summary>
        public const float RepeatWindowSeconds = 10f;

        private struct Recent
        {
            public DateTime FirstAt;
            public int Count;

            // Kept so a summary can be written without the caller still being around to supply
            // them: a fold usually ends long after the thing that made it stopped happening.
            public LogLevel Level;
            public string Category;
            public string Message;
        }

        private readonly Dictionary<string, Recent> _recent = new Dictionary<string, Recent>();

        /// <summary>Where lines go. Replaceable so a test can read them without a console.</summary>
        public Action<string> Sink = line => Console.Out.WriteLine(line);

        /// <summary>Counted since start, so a status line can answer "was it healthy" without grep.</summary>
        public int Errors { get; private set; }

        public int Warnings { get; private set; }

        /// <summary>
        /// One line: when, how bad, which part of the game, and what happened.
        ///
        /// The level comes before the message so that a prefix match is enough. Somebody hunting
        /// through two hundred megabytes reaches for grep first, and a level placed after free text
        /// cannot be matched without also matching every message that mentions the word.
        /// </summary>
        public static string Format(LogLevel level, string category, string message, DateTime atUtc)
        {
            return $"{atUtc:yyyy-MM-ddTHH:mm:ss}Z {Word(level)} [{category}] {message}";
        }

        private static string Word(LogLevel level)
        {
            switch (level)
            {
                case LogLevel.Error: return "ERROR";
                case LogLevel.Warn: return "WARN";
                default: return "INFO";
            }
        }

        public void Write(LogLevel level, string category, string message)
        {
            Write(level, category, message, DateTime.UtcNow);
        }

        /// <summary>The clock is a parameter so the folding can be tested without waiting.</summary>
        public void Write(LogLevel level, string category, string message, DateTime atUtc)
        {
            if (level == LogLevel.Error) Errors++;
            else if (level == LogLevel.Warn) Warnings++;

            // Keyed on the whole line's identity, not just the text. The same words from two
            // different parts of the game are two different facts, and folding them together would
            // hide the second failure in a cascade - usually the one that explains the first.
            string key = (int)level + "|" + category + "|" + message;

            if (_recent.TryGetValue(key, out Recent seen) &&
                (atUtc - seen.FirstAt).TotalSeconds < RepeatWindowSeconds)
            {
                seen.Count++;
                _recent[key] = seen;
                return;
            }

            // A line that stood for others says so, when the fold it ended is handed over.
            if (seen.Count > 1) Summarise(key, seen, atUtc);

            _recent[key] = new Recent { FirstAt = atUtc, Count = 1, Level = level,
                Category = category, Message = message };

            Sink?.Invoke(Format(level, category, message, atUtc));
        }

        /// <summary>
        /// Emits the tally for any fold whose window has closed. Call once a tick.
        ///
        /// Without it a burst that stops is never summarised at all: the count is only reported
        /// when the next identical line escapes the fold, so a system that threw four thousand
        /// times and then recovered leaves one line in the log claiming it happened once. That is
        /// the shape of every incident that gets closed as "could not reproduce".
        /// </summary>
        public void Flush(DateTime atUtc)
        {
            _expired.Clear();

            foreach (KeyValuePair<string, Recent> kv in _recent)
            {
                if ((atUtc - kv.Value.FirstAt).TotalSeconds < RepeatWindowSeconds) continue;
                _expired.Add(kv.Key);
            }

            for (int i = 0; i < _expired.Count; i++)
            {
                Recent seen = _recent[_expired[i]];
                _recent.Remove(_expired[i]);

                if (seen.Count > 1) Summarise(_expired[i], seen, atUtc);
            }
        }

        private readonly List<string> _expired = new List<string>();

        private void Summarise(string key, Recent seen, DateTime atUtc)
        {
            // Counted from the second, because the first was already written in full. Saying "x50"
            // after a line that was itself one of the fifty would be off by one in the direction
            // that matters: it is the number somebody quotes back when asking how bad it was.
            Sink?.Invoke(Format(seen.Level, seen.Category,
                $"{seen.Message} (x{seen.Count} in {RepeatWindowSeconds:0} s)", atUtc));
        }

        /// <summary>One line for a status report or a health endpoint.</summary>
        public string Describe()
        {
            return Errors == 0 && Warnings == 0
                ? "clean"
                : $"{Errors} error(s), {Warnings} warning(s)";
        }
    }
}
