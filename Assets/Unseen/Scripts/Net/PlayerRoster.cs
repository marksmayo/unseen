using System.Collections.Generic;

namespace Unseen.Net
{
    /// <summary>
    /// The names currently in use in a match, and the authority on who gets to be called what.
    ///
    /// Separate from <see cref="PlayerName"/> because sanitising is a property of a string and
    /// uniqueness is a property of a room: the same name is fine in one match and taken in another.
    /// The server owns one of these; a client never sees it.
    /// </summary>
    public sealed class PlayerRoster
    {
        private readonly HashSet<string> _taken = new HashSet<string>();

        /// <summary>How many names are currently held.</summary>
        public int Count => _taken.Count;

        /// <summary>
        /// Settles on the name this client will actually be shown as, and reserves it.
        ///
        /// Two identical labels in a game whose whole loop is recognising people is an
        /// impersonation tool rather than a cosmetic annoyance, so the second claimant is always
        /// made distinguishable - including when every client asked for nothing at all and they all
        /// sanitise down to the same fallback.
        /// </summary>
        public string Claim(string requested)
        {
            string basis = PlayerName.Sanitise(requested);

            // Taken is decided on what a name looks like, not on what it is. Two strings that draw
            // the same picture are the same name as far as a player across a courtyard is
            // concerned, and that is the only view that matters here.
            if (_taken.Add(PlayerName.Skeleton(basis))) return basis;

            for (int n = 2; ; n++)
            {
                string suffix = "-" + n;

                // The suffix has to fit inside the limit, not reach past it. Appending blindly
                // would produce a name longer than MaxLength that had already passed the length
                // check - a later step undoing a guarantee an earlier one made.
                string trimmed = basis.Length + suffix.Length > PlayerName.MaxLength
                    ? basis.Substring(0, PlayerName.MaxLength - suffix.Length)
                    : basis;

                string candidate = trimmed + suffix;
                if (_taken.Add(PlayerName.Skeleton(candidate))) return candidate;
            }
        }

        /// <summary>Gives a name back when its player leaves.</summary>
        public void Release(string name)
        {
            // Released by skeleton too, or a name freed by the player who held it stays blocked for
            // ever - the set is keyed on appearance, so it has to be unkeyed the same way.
            _taken.Remove(PlayerName.Skeleton(name));
        }
    }
}
