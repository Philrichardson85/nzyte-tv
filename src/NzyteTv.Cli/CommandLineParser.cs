namespace NzyteTv.Cli;

public enum CommandKind
{
    RootHelp,
    Inspect,
    Normalize,
    NormalizeLibrary,
    Verify,
}

public sealed record ParsedCommand(
    CommandKind Kind,
    string? Input = null,
    bool Overwrite = false,
    bool ShowHelp = false,
    string? Destination = null);

public sealed record CommandParseResult(ParsedCommand? Command, string? Error)
{
    public bool IsSuccess => Command is not null;
}

public static class CommandLineParser
{
    public static CommandParseResult Parse(IReadOnlyList<string> args)
    {
        if (args.Count == 0 || (args.Count == 1 && IsHelp(args[0])))
        {
            return Success(new ParsedCommand(CommandKind.RootHelp, ShowHelp: true));
        }

        CommandKind? kind = args[0].ToLowerInvariant() switch
        {
            "inspect" => CommandKind.Inspect,
            "normalize" => CommandKind.Normalize,
            "normalize-library" => CommandKind.NormalizeLibrary,
            "verify" => CommandKind.Verify,
            _ => null,
        };

        if (kind is null)
        {
            return Failure($"Unknown command '{args[0]}'.");
        }

        if (args.Count == 2 && IsHelp(args[1]))
        {
            return Success(new ParsedCommand(kind.Value, ShowHelp: true));
        }

        bool overwrite = false;
        bool optionsEnded = false;
        var inputs = new List<string>();

        foreach (string argument in args.Skip(1))
        {
            if (!optionsEnded && argument == "--")
            {
                optionsEnded = true;
            }
            else if (!optionsEnded && argument == "--overwrite")
            {
                if (kind is not (CommandKind.Normalize or CommandKind.NormalizeLibrary))
                {
                    return Failure("--overwrite is valid only for normalize and normalize-library.");
                }

                overwrite = true;
            }
            else if (!optionsEnded && argument.StartsWith("-", StringComparison.Ordinal))
            {
                return Failure($"Unknown option '{argument}'.");
            }
            else
            {
                inputs.Add(argument);
            }
        }

        int requiredInputs = kind == CommandKind.NormalizeLibrary ? 2 : 1;
        if (inputs.Count != requiredInputs)
        {
            string requirement = requiredInputs == 1
                ? "exactly one input file"
                : "a source root and a destination root";
            return Failure($"The {args[0]} command requires {requirement}.");
        }

        return Success(new ParsedCommand(
            kind.Value,
            inputs[0],
            overwrite,
            Destination: inputs.Count > 1 ? inputs[1] : null));
    }

    private static bool IsHelp(string argument) => argument is "--help" or "-h";

    private static CommandParseResult Success(ParsedCommand command) => new(command, null);

    private static CommandParseResult Failure(string error) => new(null, error);
}
