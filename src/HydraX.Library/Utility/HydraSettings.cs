using System.Collections.Generic;
using System.IO;
using System.Text;

namespace HydraX.Library
{
    /// <summary>
    /// The app's settings: a flat string -> string map, stored as JSON next to
    /// the executable.
    /// </summary>
    /// <remarks>
    /// Read and written by hand rather than through a JSON library — every
    /// value is a plain string, and it keeps the tool dependency-free.
    /// </remarks>
    public class HydraSettings
    {
        /// <summary>
        /// Setting Values
        /// </summary>
        private Dictionary<string, string> Values = new Dictionary<string, string>();

        /// <summary>
        /// Gets the setting with the given name, if not found, returns defaultVal
        /// </summary>
        public string this[string key, string defaultVal]
        {
            get
            {
                if (!Values.TryGetValue(key, out var val))
                {
                    val = defaultVal;
                    Values[key] = val;
                }

                return val;
            }
        }

        /// <summary>
        /// Sets the setting with the given name
        /// </summary>
        public string this[string key]
        {
            set
            {
                Values[key] = value;
            }
        }

        /// <summary>
        /// Initializes an instance of the Settings Class
        /// </summary>
        public HydraSettings() { }

        /// <summary>
        /// Initializes an instance of the Settings Class and loads the settings
        /// </summary>
        /// <param name="fileName">File Name</param>
        public HydraSettings(string fileName)
        {
            Load(fileName);
        }

        /// <summary>
        /// Loads Settings from a file
        /// </summary>
        /// <param name="fileName">File Name</param>
        public void Load(string fileName)
        {
            try
            {
                Values = Parse(File.ReadAllText(fileName));
            }
            catch
            {
                Save(fileName);
            }
        }

        /// <summary>
        /// Saves all settings to a file
        /// </summary>
        /// <param name="fileName">File Name</param>
        public void Save(string fileName)
        {
            try
            {
                var output = new StringBuilder("{\n");
                int i = 0;

                foreach (var value in Values)
                {
                    output.AppendFormat("    \"{0}\": \"{1}\"{2}\n",
                        Escape(value.Key),
                        Escape(value.Value ?? ""),
                        ++i < Values.Count ? "," : "");
                }

                output.Append("}");

                File.WriteAllText(fileName, output.ToString());
            }
            catch
            {
                return;
            }
        }

        /// <summary>
        /// Reads a flat JSON object of string values
        /// </summary>
        private static Dictionary<string, string> Parse(string input)
        {
            var results = new Dictionary<string, string>();
            var strings = new List<string>();
            int index = 0;

            while (index < input.Length)
            {
                if (input[index++] != '"')
                    continue;

                var current = new StringBuilder();

                while (index < input.Length && input[index] != '"')
                {
                    if (input[index] == '\\' && index + 1 < input.Length)
                    {
                        index++;

                        switch (input[index])
                        {
                            case 'n': current.Append('\n'); break;
                            case 'r': current.Append('\r'); break;
                            case 't': current.Append('\t'); break;
                            default: current.Append(input[index]); break;
                        }
                    }
                    else
                    {
                        current.Append(input[index]);
                    }

                    index++;
                }

                index++;
                strings.Add(current.ToString());
            }

            // Keys and values alternate; a trailing key with no value is dropped
            for (int i = 0; i + 1 < strings.Count; i += 2)
                results[strings[i]] = strings[i + 1];

            return results;
        }

        /// <summary>
        /// Escapes a string for writing into JSON
        /// </summary>
        private static string Escape(string value)
        {
            return value
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\r", "\\r")
                .Replace("\n", "\\n")
                .Replace("\t", "\\t");
        }
    }
}
