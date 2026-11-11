using System;

namespace Xml2
{
    /// <summary>
    /// Shared helpers for the "slash-separated path" addressing scheme
    /// used by every XmlSettings tier, so the rules exist in exactly one
    /// place and callers can rely on them once.
    ///
    /// Rules:
    ///  - paths are relative to the document root: "Section/Element",
    ///    "Section/Element/Sub". The root element's own name is not part
    ///    of the path (the root name is document-specific, e.g.
    ///    "GSAToolkit" vs "StoreController");
    ///  - matching is exact and case-sensitive;
    ///  - every segment must be a non-empty well-formed XML element name
    ///    (letters, digits, '_', '-' and '.'; not starting with a digit,
    ///    '-' or '.');
    ///  - empty segments ("a//b", "a/", "") are caller mistakes and are
    ///    rejected with ArgumentException;
    ///  - each segment resolves to at most one child — the FIRST matching
    ///    child wins. Addressing repeats by index is the record tier's
    ///    job, not path resolution's.
    ///
    /// Caller conventions for the tiers built on this:
    ///  - null path      -> ArgumentNullException
    ///  - bad segment    -> ArgumentException
    /// </summary>
    internal static class SettingPaths
    {
        private static readonly char[] Slash = new char[] { '/' };

        /// <summary>
        /// Validates a caller-supplied path. Null-safe; throws
        /// ArgumentNullException or ArgumentException with a message
        /// naming the offending part.
        /// </summary>
        public static void Validate(string path, string paramName)
        {
            if (path == null)
            {
                throw new ArgumentNullException(paramName);
            }

            string[] segments = path.Split(Slash, StringSplitOptions.None);
            for (int i = 0; i < segments.Length; i++)
            {
                string segment = segments[i];

                if (segment.Length == 0)
                {
                    throw new ArgumentException(
                        "Path '" + path + "' contains an empty segment.",
                        paramName);
                }

                if (!XmlNames.IsWellFormedName(segment))
                {
                    throw new ArgumentException(
                        "Path segment '" + segment + "' (in '" + path +
                        "') is not a well-formed XML element name.",
                        paramName);
                }
            }
        }

        /// <summary>
        /// Validates a caller-supplied element or attribute name: it must
        /// be non-null, non-empty, and a well-formed XML name. Used by the
        /// attribute tier for attribute names (the path tier already checks
        /// its segments inline).
        /// </summary>
        public static void ValidateName(string name, string paramName)
        {
            if (name == null)
            {
                throw new ArgumentNullException(paramName);
            }

            if (name.Length == 0 || !XmlNames.IsWellFormedName(name))
            {
                throw new ArgumentException(
                    "'" + name + "' is not a well-formed XML name.",
                    paramName);
            }
        }

        /// <summary>
        /// Splits a path into segments. Callers must have run
        /// <see cref="Validate"/> first (no null, no empty segments).
        /// </summary>
        public static string[] Split(string path)
        {
            return path.Split(Slash, StringSplitOptions.None);
        }
    }
}
