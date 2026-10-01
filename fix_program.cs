using System;
using System.IO;
using System.Text.RegularExpressions;

class FixProgram {
    static void Main() {
        string path = @"C:\DEV\TOLIK_QWEN\src\TOLIK.Core.Host\Program.cs";
        string content = File.ReadAllText(path, System.Text.Encoding.UTF8);
        
        // Fix the broken $"" strings
        content = content.Replace("\\$\\\"", "$\"");
        content = content.Replace("\\$\\\"", "$\"");
        content = content.Replace("\\$\\\"", "$\"");
        
        // Fix any remaining double-escaped dollar signs
        content = Regex.Replace(content, @"\$\$", "$");
        
        File.WriteAllText(path, content, System.Text.Encoding.UTF8);
        Console.WriteLine("Fixed");
    }
}