using System.CommandLine;

var root = new RootCommand("Dictum: voice to text.");

return root.Parse(args).Invoke();
