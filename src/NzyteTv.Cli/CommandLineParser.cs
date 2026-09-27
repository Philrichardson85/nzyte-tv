using NzyteTv.Core;

namespace NzyteTv.Cli;

public enum CommandKind
{
    RootHelp,
    Inspect,
    Normalize,
    NormalizeLibrary,
    Verify,
    MetadataHelp,
    MetadataInitialize,
    MetadataReview,
    MetadataSync,
    MetadataRebind,
    MetadataEdit,
}

public sealed record ParsedCommand(
    CommandKind Kind,
    string? Input = null,
    bool Overwrite = false,
    bool ShowHelp = false,
    string? Destination = null,
    string? CatalogPath = null,
    bool DryRun = false,
    string? MetadataType = null,
    string? MetadataSubtype = null);

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

        if (string.Equals(args[0], "metadata", StringComparison.OrdinalIgnoreCase))
        {
            return ParseMetadata(args);
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

    private static CommandParseResult ParseMetadata(IReadOnlyList<string> args)
    {
        if (args.Count == 1 || (args.Count == 2 && IsHelp(args[1])))
        {
            return Success(new ParsedCommand(CommandKind.MetadataHelp, ShowHelp: true));
        }

        CommandKind? kind = args[1].ToLowerInvariant() switch
        {
            "initialize" => CommandKind.MetadataInitialize,
            "review" => CommandKind.MetadataReview,
            "sync" => CommandKind.MetadataSync,
            "rebind" => CommandKind.MetadataRebind,
            "edit" => CommandKind.MetadataEdit,
            _ => null,
        };
        if (kind is null)
        {
            return Failure($"Unknown metadata command '{args[1]}'.");
        }

        if (args.Count == 3 && IsHelp(args[2]))
        {
            return Success(new ParsedCommand(kind.Value, ShowHelp: true));
        }

        bool dryRun = false;
        bool optionsEnded = false;
        string? catalogPath = null;
        string? metadataType = null;
        string? metadataSubtype = null;
        var inputs = new List<string>();
        for (int index = 2; index < args.Count; index++)
        {
            string argument = args[index];
            if (!optionsEnded && argument == "--")
            {
                optionsEnded = true;
            }
            else if (!optionsEnded && argument == "--dry-run")
            {
                if (kind != CommandKind.MetadataInitialize)
                {
                    return Failure("--dry-run is valid only for metadata initialize.");
                }

                dryRun = true;
            }
            else if (!optionsEnded && argument == "--catalog")
            {
                if (kind is not (CommandKind.MetadataInitialize or CommandKind.MetadataReview))
                {
                    return Failure("--catalog is valid only for metadata initialize and metadata review.");
                }

                if (++index >= args.Count
                    || string.IsNullOrWhiteSpace(args[index])
                    || args[index].StartsWith("-", StringComparison.Ordinal))
                {
                    return Failure("--catalog requires a catalog path.");
                }

                catalogPath = args[index];
            }
            else if (!optionsEnded && argument is "--type" or "--subtype")
            {
                if (kind != CommandKind.MetadataEdit)
                {
                    return Failure($"{argument} is valid only for metadata edit.");
                }

                if (++index >= args.Count
                    || string.IsNullOrWhiteSpace(args[index])
                    || args[index].StartsWith("-", StringComparison.Ordinal))
                {
                    return Failure($"{argument} requires a value.");
                }

                if (argument == "--type")
                {
                    metadataType = args[index];
                }
                else
                {
                    metadataSubtype = args[index];
                }
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

        int requiredInputs = kind == CommandKind.MetadataEdit ? 1 : 2;
        if (inputs.Count != requiredInputs)
        {
            string requirement = kind switch
            {
                CommandKind.MetadataRebind => "an old source path and a new source path",
                CommandKind.MetadataEdit => "one source media path",
                _ => "a source root and a library root",
            };
            return Failure($"The metadata {args[1]} command requires {requirement}.");
        }

        if (kind is CommandKind.MetadataInitialize or CommandKind.MetadataReview
            && string.IsNullOrWhiteSpace(catalogPath))
        {
            return Failure($"The metadata {args[1]} command requires --catalog <catalog-path>.");
        }

        if (kind == CommandKind.MetadataEdit)
        {
            if (string.IsNullOrWhiteSpace(metadataType))
            {
                return Failure("The metadata edit command requires --type <type>.");
            }

            if (!AssetTypes.Supported.Contains(metadataType))
            {
                return Failure($"Unknown asset type '{metadataType}'.");
            }

            if (metadataSubtype is not null && metadataType != AssetTypes.ShortForm)
            {
                return Failure("--subtype is valid only when --type is short-form.");
            }
        }

        return Success(new ParsedCommand(
            kind.Value,
            inputs[0],
            Destination: inputs.Count > 1 ? inputs[1] : null,
            CatalogPath: catalogPath,
            DryRun: dryRun,
            MetadataType: metadataType,
            MetadataSubtype: metadataSubtype));
    }

    private static bool IsHelp(string argument) => argument is "--help" or "-h";

    private static CommandParseResult Success(ParsedCommand command) => new(command, null);

    private static CommandParseResult Failure(string error) => new(null, error);
}
