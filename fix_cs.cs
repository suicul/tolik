using System;
using System.IO;
using System.Text.RegularExpressions;

class FixProgram {
    static void Main() {
        string path = @"C:\DEV\TOLIK_QWEN\src\TOLIK.Core.Host\Program.cs";
        string content = File.ReadAllText(path, System.Text.Encoding.UTF8);
        
        // Fix the broken char literals - they got corrupted by bad string replacements
        // Look for patterns like '\$"' which is invalid (too many chars in char literal)
        content = content.Replace("\\$\\\"", "$\"");
        content = content.Replace("\\$\\\"", "$\"");
        content = content.Replace("\\$\\\"", "$\"");
        
        // Fix any remaining double-escaped dollar signs in string context
        content = Regex.Replace(content, @"\\$\$", "$");
        
        File.WriteAllText(path, content, System.Text.Encoding.UTF8);
        Console.WriteLine("Fixed");
    }
}