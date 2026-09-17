using System.Globalization;
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

            // Written in two alphabets at once, which nobody does by accident and every homoglyph
            // attack does by necessity. Refused outright rather than folded, because there is
            // nothing to fold it to: the whole point is that the character used may be one no
            // table carries, so the name cannot be made to collide with the one it imitates.
            //
            // Before the length cut, so that trimming a long name down cannot drop the foreign
            // letter off the end and let what is left through as ordinary.
            if (MixesScripts(cleaned)) return Fallback;

            if (cleaned.Length > MaxLength) cleaned = cleaned.Substring(0, MaxLength).Trim();

            return cleaned.Length == 0 ? Fallback : cleaned;
        }

        /// <summary>
        /// Folds one character onto the Latin letter it is pretending to be.
        ///
        /// What is left after NFKD has done its work. Normalisation collapses everything that is
        /// formally a variant of a letter - accents, widths, the mathematical alphabets - but a
        /// Cyrillic а is not a variant of a Latin a, it is a different letter that happens to be
        /// drawn the same, and no amount of normalisation will ever join them. Those have to be
        /// listed, and this is the list: the characters that exist in fonts everybody has and
        /// render indistinguishably from a Latin letter at the size a name is drawn.
        ///
        /// Cherokee is here because it is where people go once Cyrillic is blocked - a full set of
        /// upper-case Latin lookalikes in a script most confusables tables do not bother with.
        /// </summary>
        private static char Fold(char c)
        {
            switch (c)
            {
                // Cyrillic, lower case.
                case 'а': return 'a';
                case 'в': return 'b';
                case 'с': return 'c';
                case 'е': return 'e';
                case 'һ': return 'h';
                case 'і': return 'i';
                case 'ј': return 'j';
                case 'к': return 'k';
                case 'м': return 'm';
                case 'о': return 'o';
                case 'р': return 'p';
                case 'ѕ': return 's';
                case 'т': return 't';
                case 'х': return 'x';
                case 'у': return 'y';

                // Cyrillic, upper case. Not reachable by lower-casing the entries above, because
                // this folds before the case is dropped - deliberately, so that scripts whose
                // casing rules the runtime may not carry are still caught.
                case 'А': return 'a';
                case 'В': return 'b';
                case 'С': return 'c';
                case 'Е': return 'e';
                case 'Һ': return 'h';
                case 'Н': return 'h';
                case 'І': return 'i';
                case 'Ј': return 'j';
                case 'К': return 'k';
                case 'М': return 'm';
                case 'О': return 'o';
                case 'Р': return 'p';
                case 'Ѕ': return 's';
                case 'Т': return 't';
                case 'Х': return 'x';
                case 'У': return 'y';

                // Greek, lower case.
                case 'α': return 'a';
                case 'ε': return 'e';
                case 'ι': return 'i';
                case 'κ': return 'k';
                case 'ν': return 'v';
                case 'ο': return 'o';
                case 'ρ': return 'p';
                case 'τ': return 't';
                case 'υ': return 'u';
                case 'χ': return 'x';

                // Greek, upper case.
                case 'Α': return 'a';
                case 'Β': return 'b';
                case 'Ε': return 'e';
                case 'Ζ': return 'z';
                case 'Η': return 'h';
                case 'Ι': return 'i';
                case 'Κ': return 'k';
                case 'Μ': return 'm';
                case 'Ν': return 'n';
                case 'Ο': return 'o';
                case 'Ρ': return 'p';
                case 'Τ': return 't';
                case 'Υ': return 'y';
                case 'Χ': return 'x';

                // Cherokee.
                case 'Ꭰ': return 'd';
                case 'Ꭱ': return 'r';
                case 'Ꭲ': return 't';
                case 'Ꭵ': return 'i';
                case 'Ꭺ': return 'a';
                case 'Ꭻ': return 'j';
                case 'Ꭼ': return 'e';
                case 'Ꮃ': return 'w';
                case 'Ꮇ': return 'm';
                case 'Ꮋ': return 'h';
                case 'Ꮓ': return 'z';
                case 'Ꮩ': return 'v';
                case 'Ꮮ': return 'l';
                case 'Ꮯ': return 'c';
                case 'Ꮲ': return 'p';
                case 'Ᏻ': return 'g';
                case 'Ᏼ': return 'b';

                // Small capitals, which live in the phonetic blocks and - unlike the fullwidth and
                // mathematical alphabets - carry no compatibility decomposition at all, so
                // normalisation leaves every one of them standing.
                case 'ᴀ': return 'a';
                case 'ʙ': return 'b';
                case 'ᴄ': return 'c';
                case 'ᴅ': return 'd';
                case 'ᴇ': return 'e';
                case 'ꜰ': return 'f';
                case 'ɢ': return 'g';
                case 'ʜ': return 'h';
                case 'ɪ': return 'i';
                case 'ᴊ': return 'j';
                case 'ᴋ': return 'k';
                case 'ʟ': return 'l';
                case 'ᴍ': return 'm';
                case 'ɴ': return 'n';
                case 'ᴏ': return 'o';
                case 'ᴘ': return 'p';
                case 'ʀ': return 'r';
                case 'ꜱ': return 's';
                case 'ᴛ': return 't';
                case 'ᴜ': return 'u';
                case 'ᴠ': return 'v';
                case 'ᴡ': return 'w';
                case 'ʏ': return 'y';
                case 'ᴢ': return 'z';

                // Digits standing in for letters, which is the oldest trick of the lot.
                case '0': return 'o';
                case '1': return 'l';
                case '5': return 's';

                default: return c;
            }
        }

        /// <summary>
        /// Folds a character from above the basic plane, which normalisation here will not touch.
        ///
        /// Compatibility decomposition handles the fullwidth and circled alphabets and leaves the
        /// mathematical ones standing, because the runtime's normalisation does not decompose
        /// characters outside the basic plane - the fullwidth Ｍ folds and the mathematical 𝐌 does
        /// not. That is the single most-used styling trick there is: every "fancy text" site on
        /// the internet is a mathematical alphabet converter, and a name run through one is bold
        /// or italic or script Mark, which is to say Mark.
        ///
        /// No table needed. The block is thirteen complete copies of the Latin alphabet laid out
        /// as contiguous runs of fifty-two, so the fold is arithmetic. The gaps in the script,
        /// fraktur and double-struck runs are reserved code points, left empty precisely to keep
        /// the runs aligned - nobody can type one, and the letters that would have filled them
        /// live in Letterlike Symbols, which is inside the basic plane and folds normally.
        ///
        /// Returns NUL for anything it does not recognise, which the caller passes through whole.
        /// </summary>
        private static char FoldAstral(int codePoint)
        {
            if (codePoint >= 0x1D400 && codePoint <= 0x1D6A3)
            {
                int offset = (codePoint - 0x1D400) % 52;
                return offset < 26 ? (char)('A' + offset) : (char)('a' + offset - 26);
            }

            // The dotless i and j that close the Latin part of the block.
            if (codePoint == 0x1D6A4) return 'i';
            if (codePoint == 0x1D6A5) return 'j';

            // The digits, five runs of ten.
            if (codePoint >= 0x1D7CE && codePoint <= 0x1D7FF)
                return (char)('0' + (codePoint - 0x1D7CE) % 10);

            return '\0';
        }

        /// <summary>
        /// What a name looks like, rather than what it is - for deciding whether two are the same.
        ///
        /// This is the idea behind Unicode TR39's skeleton: fold everything that renders alike onto
        /// one representative, then compare those. "Mark" and "Mark" with a Cyrillic а are entirely
        /// different strings and an identical picture, and in a game whose loop is working out who
        /// you are looking at, letting both stand hands somebody a free disguise.
        ///
        /// Two stages, and the first does most of the work. Compatibility decomposition pulls every
        /// formal variant of a letter back to the letter: accents come off as separate marks and
        /// are then dropped, the fullwidth and mathematical and circled alphabets collapse to plain
        /// ASCII, ligatures come apart. That is several hundred confusables handled by one call,
        /// and it will go on handling the alphabets Unicode has not invented yet. Only then does
        /// the hand-written table run, for the cases normalisation cannot reach: letters from other
        /// scripts drawn the same way that are not, formally, the same letter at all.
        ///
        /// Still not the complete confusables table - that runs to thousands of entries and no
        /// hand-written list will ever be it. <see cref="MixesScripts"/> is the answer to what is
        /// missing, because it does not care which lookalike was used.
        /// </summary>
        public static string Skeleton(string name)
        {
            if (string.IsNullOrEmpty(name)) return string.Empty;

            try
            {
                name = name.Normalize(NormalizationForm.FormKD);
            }
            catch (System.ArgumentException)
            {
                // Malformed unicode - lone surrogates, which a hostile client can certainly send.
                // Folding carries on with what is there rather than the name being rejected.
            }

            var builder = new StringBuilder(name.Length);

            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];

                if (char.IsHighSurrogate(c) && i + 1 < name.Length &&
                    char.IsLowSurrogate(name[i + 1]))
                {
                    char ascii = FoldAstral(char.ConvertToUtf32(c, name[i + 1]));
                    i++;

                    // Through Fold on the way out, so that a mathematical 0 gets the same
                    // digit-for-letter treatment an ordinary one would.
                    if (ascii != '\0') builder.Append(char.ToLowerInvariant(Fold(ascii)));
                    else { builder.Append(c); builder.Append(name[i]); }

                    continue;
                }

                // The marks the decomposition just separated out. Dropping them is what turns
                // Mârk, Márk and Mãrk into one name without an entry for any of them.
                UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(c);

                if (category == UnicodeCategory.NonSpacingMark ||
                    category == UnicodeCategory.SpacingCombiningMark ||
                    category == UnicodeCategory.EnclosingMark)
                    continue;

                builder.Append(char.ToLowerInvariant(Fold(c)));
            }

            return builder.ToString();
        }

        /// <summary>
        /// Which alphabet a character belongs to, as far as this needs to care.
        ///
        /// Unicode gives every character a Script property and .NET does not expose it, so these
        /// are the ranges that matter for names. Anything that is not a letter - digits, spaces,
        /// punctuation, combining marks - is Common: it belongs to no script and appears in all of
        /// them, so it can never be the thing that makes a name mixed.
        /// </summary>
        private enum Script
        {
            Common = 0,
            Latin,
            Greek,
            Cyrillic,
            Cherokee,
            Han,
            Hiragana,
            Katakana,
            Hangul,
            Bopomofo,
            Hebrew,
            Arabic,
            Armenian,
            Georgian,
            Devanagari,
            Thai,
            Other
        }

        private static Script ScriptOf(int c)
        {
            // Latin, accented and phonetic letters included - a name is not written in another
            // alphabet for having an umlaut in it.
            if ((c >= 0x0041 && c <= 0x005A) || (c >= 0x0061 && c <= 0x007A) ||
                (c >= 0x00C0 && c <= 0x02AF) || (c >= 0x1D00 && c <= 0x1D7F) ||
                (c >= 0x1E00 && c <= 0x1EFF) || (c >= 0x2C60 && c <= 0x2C7F) ||
                (c >= 0xA720 && c <= 0xA7FF) || (c >= 0xAB30 && c <= 0xAB6F))
                return Script.Latin;

            if ((c >= 0x0370 && c <= 0x03FF) || (c >= 0x1F00 && c <= 0x1FFF)) return Script.Greek;

            if ((c >= 0x0400 && c <= 0x052F) || (c >= 0x2DE0 && c <= 0x2DFF) ||
                (c >= 0xA640 && c <= 0xA69F)) return Script.Cyrillic;

            if ((c >= 0x13A0 && c <= 0x13FD) || (c >= 0xAB70 && c <= 0xABBF))
                return Script.Cherokee;

            if (c >= 0x3041 && c <= 0x309F) return Script.Hiragana;

            if ((c >= 0x30A0 && c <= 0x30FF) || (c >= 0x31F0 && c <= 0x31FF) ||
                (c >= 0xFF66 && c <= 0xFF9F)) return Script.Katakana;

            if (c == 0x3005 || (c >= 0x2E80 && c <= 0x2EFF) || (c >= 0x3400 && c <= 0x4DBF) ||
                (c >= 0x4E00 && c <= 0x9FFF) || (c >= 0xF900 && c <= 0xFAFF) ||
                (c >= 0x20000 && c <= 0x2FA1F)) return Script.Han;

            if ((c >= 0x3100 && c <= 0x312F) || (c >= 0x31A0 && c <= 0x31BF))
                return Script.Bopomofo;

            if ((c >= 0x1100 && c <= 0x11FF) || (c >= 0x3130 && c <= 0x318F) ||
                (c >= 0xA960 && c <= 0xA97F) || (c >= 0xAC00 && c <= 0xD7FB))
                return Script.Hangul;

            if (c >= 0x0590 && c <= 0x05FF) return Script.Hebrew;

            if ((c >= 0x0600 && c <= 0x06FF) || (c >= 0x0750 && c <= 0x077F) ||
                (c >= 0xFB50 && c <= 0xFDFF) || (c >= 0xFE70 && c <= 0xFEFF)) return Script.Arabic;

            if (c >= 0x0530 && c <= 0x058F) return Script.Armenian;

            if ((c >= 0x10A0 && c <= 0x10FF) || (c >= 0x1C90 && c <= 0x1CBF))
                return Script.Georgian;

            if (c >= 0x0900 && c <= 0x097F) return Script.Devanagari;
            if (c >= 0x0E00 && c <= 0x0E7F) return Script.Thai;

            // Letters from a script not named above all count as one lump, so two such scripts
            // together read as one and go unnoticed. That is a known gap, and it is the right way
            // round: wrongly accusing somebody of not writing their own name is the worse mistake.
            if (c <= 0xFFFF && char.IsLetter((char)c)) return Script.Other;

            return Script.Common;
        }

        private static int Bit(Script script)
        {
            return 1 << (int)script;
        }

        /// <summary>Scripts written together as a matter of course, by the language that does it.</summary>
        private static readonly int Japanese =
            Bit(Script.Latin) | Bit(Script.Han) | Bit(Script.Hiragana) | Bit(Script.Katakana);

        private static readonly int Chinese =
            Bit(Script.Latin) | Bit(Script.Han) | Bit(Script.Bopomofo);

        private static readonly int Korean =
            Bit(Script.Latin) | Bit(Script.Han) | Bit(Script.Hangul);

        /// <summary>
        /// Whether a name draws on alphabets that are not written together.
        ///
        /// The general defence, and the reason the table above does not have to be complete. An
        /// exhaustive confusables list is an arms race worth losing: every entry buys one
        /// character, and Unicode keeps making more of them. This asks a different question - not
        /// "is this character a known lookalike" but "is this name written in two alphabets at
        /// once", which is what all of those attacks have in common and what nobody does by
        /// accident. Whichever character was used, and whether or not anyone has catalogued it,
        /// the mixture shows.
        ///
        /// Scripts that genuinely are written together are exempt, and that matters here more than
        /// in most places this rule gets implemented: Japanese uses three at once as a matter of
        /// course, and the game is set in a Japanese town. A rule that flagged 忍者ニンジャ would be
        /// a rule that punished the game's own audience for spelling their language correctly.
        /// </summary>
        public static bool MixesScripts(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;

            int present = 0;

            for (int i = 0; i < name.Length; i++)
            {
                int codePoint = name[i];

                // Surrogate pairs, so the Han extensions - which live above the basic plane - are
                // read as one character rather than as two unassigned halves.
                if (char.IsHighSurrogate(name[i]) && i + 1 < name.Length &&
                    char.IsLowSurrogate(name[i + 1]))
                {
                    codePoint = char.ConvertToUtf32(name[i], name[i + 1]);
                    i++;
                }

                present |= Bit(ScriptOf(codePoint));
            }

            present &= ~Bit(Script.Common);

            if (present == 0) return false;

            // A single bit set: one script, which is what every ordinary name is.
            if ((present & (present - 1)) == 0) return false;

            if ((present & ~Japanese) == 0) return false;
            if ((present & ~Chinese) == 0) return false;
            if ((present & ~Korean) == 0) return false;

            return true;
        }
    }
}
