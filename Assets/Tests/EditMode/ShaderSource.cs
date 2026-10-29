using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;

namespace Sonoma.Tests
{
    // A shader's effective source: Assets/Shaders/<name> with every #include "Sonoma*.hlsl" spliced
    // in where it stands, recursively and once each, as the include guards leave it for the
    // compiler. URP's includes are left alone. The source-text guards read this rather than the
    // .shader file, so code moved into an include (ocean-a step 1) is checked where it is compiled,
    // and no test can check one file and miss the other.
    //
    // Editor-only: it needs Application.dataPath to find the shader.
    static class ShaderSource
    {
        static readonly Regex Include =
            new Regex(@"^[ \t]*#include\s+""(Sonoma[^""]*\.hlsl)""[^\n]*$", RegexOptions.Multiline);

        public static string Load(string name)
        {
            string path = Path.Combine(Application.dataPath, "Shaders", name);
            Assert.IsTrue(File.Exists(path), $"shader not found at {path}");
            return Splice(path, new HashSet<string>());
        }

        static string Splice(string path, HashSet<string> seen)
        {
            string dir = Path.GetDirectoryName(path);
            return Include.Replace(File.ReadAllText(path), m =>
            {
                string include = Path.GetFullPath(Path.Combine(dir, m.Groups[1].Value));
                Assert.IsTrue(File.Exists(include),
                    $"{m.Groups[1].Value}, included from {Path.GetFileName(path)}, is not next to it");
                return seen.Add(include) ? Splice(include, seen) : "";
            });
        }
    }
}
