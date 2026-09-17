using System.Text;

namespace Unseen.Net
{
    /// <summary>
    /// Turns whatever a client claims its name is into something safe to show other players.
    ///
    /// This is the only attacker-controlled text in the game that reaches other people's screens,
    /// so the rule is that a client's name is a suggestion and the server decides what anybody
    /// actually sees. Kept free of the transport and the simulation so every hostile string can be
    /// written as a plain call.
    /// </summary>
    public static class PlayerName
    {
        /// <summary>
        /// Longest name the server will show. A label over an agent's head has a width the screen
        /// decides, not the player, and a name is not a place to put a payload.
        /// </summary>
        public const int MaxLength = 16;

        /// <summary>
        /// What an unnameable client is called. An agent with no visible label is worse than one
        /// with an ugly name in a game where recognising people is the point - and a client can
        /// send an empty name as easily as any other, so this is a normal path, not an error.
        /// </summary>
        public const string Fallback = "ninja";

        /// <summary>
        /// Whether a character is invisible or rewrites how text around it is drawn.
        ///
        /// None of these are control characters, so char.IsControl misses every one. They matter
        /// for two different reasons. Zero-width characters let a name be padded into a different
        /// string that draws identically to somebody else's - impersonation for free. The bidi
        /// overrides and isolates are worse: they change the rendering direction of the text
        /// around them, so a name can reorder a line it merely appears in.
        /// </summary>
        private static bool IsInvisibleOrDirectional(char c)
        {
            return c == '​' || c == '‌' || c == '‍' || c == '﻿' ||
                   (c >= '‪' && c <= '‮') ||
                   (c >= '⁦' && c <= '⁩') ||
                   c == '­';
        }

        /// <summary>The name to display for a client that asked to be called this.</summary>
        public static string Sanitise(string requested)
        {
            if (string.IsNullOrEmpty(requested)) return Fallback;

            // Normalised before anything is measured or compared.
            //
            // The same visible name can be encoded several ways - a letter with an accent as one
            // code point or as two, a full-width Latin letter beside its ordinary twin. NFKC folds
            // those into one form, so a length check counts what a reader would count and two names
            // that look the same are the same string before uniqueness ever sees them.
            try
            {
                requested = requested.Normalize(System.Text.NormalizationForm.FormKC);
            }
            catch (System.ArgumentException)
            {
                // Malformed unicode - lone surrogates, which a hostile client can certainly send.
                // The characters survive unnormalised rather than the name being rejected outright.
            }

            // Control characters go, all of them.
            //
            // A line break is the one with an attack behind it: the kill feed and the server log are
            // both line-oriented, so a newline inside a name lets a player append a line of their
            // own that appears to have come from somewhere else. The rest - nulls, bells, escapes -
            // have no business in a name either, and stripping the whole category is both simpler
            // to reason about and harder to find a gap in than naming the dangerous ones.
            var builder = new StringBuilder(requested.Length);

            for (int i = 0; i < requested.Length; i++)
            {
                char c = requested[i];
                if (!char.IsControl(c) && !IsInvisibleOrDirectional(c)) builder.Append(c);
            }

            // Order matters. Strip first, then trim, then cut to length, then trim again.
            //
            // Cutting before stripping would let a long run of control characters spend the whole
            // budget and leave an empty name that had nonetheless passed a length check. The second
            // trim is for the case where the cut lands mid-space and leaves a trailing one.
            string cleaned = builder.ToString().Trim();

            if (cleaned.Length > MaxLength) cleaned = cleaned.Substring(0, MaxLength).Trim();

            return cleaned.Length == 0 ? Fallback : cleaned;
        }

        /// <summary>
        /// What a name looks like, rather than what it is - for deciding whether two are the same.
        ///
        /// This is the idea behind Unicode TR39's skeleton: fold everything that renders alike onto
        /// one representative, then compare those. "Mark" and "Mark" with a Cyrillic а are entirely
        /// different strings and an identical picture, and in a game whose loop is working out who
        /// you are looking at, letting both stand hands somebody a free disguise.
        ///
        /// Deliberately partial. The full confusables table runs to thousands of entries; this
        /// carries the Cyrillic and Greek letters that account for nearly all real abuse, because
        /// they are the ones that exist in fonts everybody has. Somebody determined, with an obscure
        /// script, can still find a gap - this raises the cost a great deal rather than closing the
        /// door, and pretending otherwise would be worse than saying so.
        /// </summary>
        public static string Skeleton(string name)
        {
            if (string.IsNullOrEmpty(name)) return string.Empty;

            var builder = new StringBuilder(name.Length);

            foreach (char c in name.ToLowerInvariant())
            {
                switch (c)
                {
                    // Cyrillic lookalikes.
                    case 'а': builder.Append('a'); break;
                    case 'е': builder.Append('e'); break;
                    case 'о': builder.Append('o'); break;
                    case 'р': builder.Append('p'); break;
                    case 'с': builder.Append('c'); break;
                    case 'х': builder.Append('x'); break;
                    case 'у': builder.Append('y'); break;
                    case 'і': builder.Append('i'); break;
                    case 'ј': builder.Append('j'); break;
                    case 'һ': builder.Append('h'); break;
                    case 'ѕ': builder.Append('s'); break;

                    // Greek lookalikes.
                    case 'α': builder.Append('a'); break;
                    case 'ο': builder.Append('o'); break;
                    case 'ρ': builder.Append('p'); break;
                    case 'ν': builder.Append('v'); break;
                    case 'υ': builder.Append('u'); break;

                    // Digits standing in for letters, which is the oldest trick of the lot.
                    case '0': builder.Append('o'); break;
                    case '1': builder.Append('l'); break;
                    case '5': builder.Append('s'); break;

                    default: builder.Append(c); break;
                }
            }

            return builder.ToString();
        }
    }
}
