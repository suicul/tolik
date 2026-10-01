using System;
using System.IO;
using System.Text.RegularExpressions;

class FixProgram {
    static void Main() {
        string path = @"C:\DEV\TOLIK_QWEN\src\TOLIK.Core.Host\Program.cs";
        string content = File.ReadAllText(path, System.Text.Encoding.UTF8);
        
        // Fix broken char literals like '\$"' -> should be '$' or '$' as string
        // The pattern is: '...' where content is more than one char
        content = Regex.Replace(content, @"'\\?\$[^']*'", m => {
            string val = m.Value;
            // Extract the actual character intended
            if (val.Contains('$')) return "\"$\"";
            return val;
        });
        
        // Also fix the broken $"" strings
        content = content.Replace("\\$\\\"", "$\"");
        content = content.Replace("\\$\\\"", "$\"");
        content = content.Replace("\\$\\\"", "$\"");
        content = Regex.Replace(content, @"\\$\$", "$");
        
        File.WriteAllText(@"C:\DEV\TOLIK_QWEN\src\TOLIK.Core.Host\Program.cs", content, System.Text.Encoding.UTF8);
        Console.WriteLine("Fixed");
    }
}