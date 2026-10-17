// FakeTool: behaves like whatever program it is copied as. Next to the executable lives
// "<executable>.fake.json", a list of rules; the first rule whose "match" strings all occur in the
// arguments decides what happens. Every invocation is appended to "<executable>.calls.log" (one JSON
// array of arguments per line) so tests can assert on what was run.
//
// Rule fields (all optional):
//   match            strings that must each occur in some argument
//   stdout, stderr   text written once
//   stdoutLines      lines written one at a time, lineDelayMs apart
//   writeArgPattern  regex over the joined arguments; group 1 is a path that gets fileContent
//   writeLastArg     write fileContent to the last argument (an output file)
//   fileContent      text for the files above
//   delayMs          wait before exiting
//   hang             never exit on its own (tests cancel it)
//   exit             exit code (default 0)
using System.Text.Json;
using System.Text.RegularExpressions;

string self = Environment.ProcessPath!;
string scriptPath = self + ".fake.json";
string[] arguments = args;
File.AppendAllText(self + ".calls.log", JsonSerializer.Serialize(arguments) + "\n");

if (!File.Exists(scriptPath))
{
    Console.Error.WriteLine($"FakeTool: no script at {scriptPath}");
    return 99;
}

using JsonDocument script = JsonDocument.Parse(File.ReadAllText(scriptPath));
JsonElement? rule = null;
foreach (JsonElement candidate in script.RootElement.EnumerateArray())
{
    bool matches = !candidate.TryGetProperty("match", out JsonElement match) ||
                   match.EnumerateArray().All(m => arguments.Any(a => a.Contains(m.GetString()!, StringComparison.Ordinal)));
    if (matches)
    {
        rule = candidate;
        break;
    }
}

if (rule is not { } r)
{
    Console.Error.WriteLine("FakeTool: no rule matches " + string.Join(' ', arguments));
    return 98;
}

string Text(string name) => r.TryGetProperty(name, out JsonElement value) ? value.GetString() ?? string.Empty : string.Empty;
int Number(string name, int fallback) => r.TryGetProperty(name, out JsonElement value) ? value.GetInt32() : fallback;

string content = Text("fileContent");
if (Text("writeArgPattern") is { Length: > 0 } pattern &&
    Regex.Match(string.Join('\u001f', arguments), pattern) is { Success: true } found)
{
    File.WriteAllText(found.Groups[1].Value, content);
}

if (r.TryGetProperty("writeLastArg", out JsonElement writeLast) && writeLast.GetBoolean() && arguments.Length > 0)
{
    File.WriteAllText(arguments[^1], content);
}

Console.Out.Write(Text("stdout"));
Console.Error.Write(Text("stderr"));
if (r.TryGetProperty("stdoutLines", out JsonElement lines))
{
    int lineDelay = Number("lineDelayMs", 0);
    foreach (JsonElement line in lines.EnumerateArray())
    {
        Console.Out.WriteLine(line.GetString());
        Console.Out.Flush();
        if (lineDelay > 0)
        {
            Thread.Sleep(lineDelay);
        }
    }
}

Console.Out.Flush();
Console.Error.Flush();
if (r.TryGetProperty("hang", out JsonElement hang) && hang.GetBoolean())
{
    Thread.Sleep(Timeout.Infinite);
}

Thread.Sleep(Number("delayMs", 0));
return Number("exit", 0);
