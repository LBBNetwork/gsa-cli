using System;

namespace Xml2
{
    /// <summary>
    /// Shared NCName-style well-formedness check for element and attribute
    /// names (letters, digits, '_', '-' and '.'; must not start with a
    /// digit, '-' or '.'). Namespaced names (with ':') are not in the v1
    /// scope and are treated as invalid.
    ///
    /// Used both at write time (precise pre-flush diagnostics) and at
    /// mutation time (fail-fast on obviously bad names).
    /// </summary>
    internal static class XmlNames
    {
        public static bool IsWellFormedName(string name)
        {
            if (name == null || name.Length == 0)
            {
                return false;
            }

            if (IsNameStart(name[0]))
            {
                for (int i = 1; i < name.Length; i++)
                {
                    if (!IsNameBody(name[i]))
                    {
                        return false;
                    }
                }

                return true;
            }

            return false;
        }

        private static bool IsNameStart(char c)
        {
            return c == '_' || char.IsLetter(c);
        }

        private static bool IsNameBody(char c)
        {
            return c == '_' || c == '-' || c == '.' || char.IsLetter(c) || char.IsDigit(c);
        }
    }
}
