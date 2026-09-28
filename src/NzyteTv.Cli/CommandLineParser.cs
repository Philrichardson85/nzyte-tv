using System.Globalization;
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
    BuildPlaylist,
    MediaHelp,
    MediaInit,
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
    string? MetadataSubtype = null,
    string? OutputPath = null,
    TimeSpan? TargetDuration = null,
    int? Seed = null,
    string? HistoryPath = null,
    VerticalLayoutMode VerticalLayout = VerticalLayoutMode.None);

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

        if (string.Equals(args[0], "media", StringComparison.OrdinalIgnoreCase))
        {
            return ParseMedia(args);
        }

        if (string.Equals(args[0], "build-playlist", StringComparison.OrdinalIgnoreCase))
        {
            return ParseBuildPlaylist(args);
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
        VerticalLayoutMode verticalLayout = VerticalLayoutMode.None;
        var inputs = new List<string>();

        for (int index = 1; index < args.Count; index++)
        {
            string argument = args[index];
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
            else if (!optionsEnded && argument == "--vertical-layout")
            {
                if (kind is not (CommandKind.Normalize or CommandKind.NormalizeLibrary))
                {
                    return Failure("--vertical-layout is valid only for normalize and normalize-library.");
                }

                if (++index >= args.Count || string.IsNullOrWhiteSpace(args[index]))
                {
                    return Failure("--vertical-layout requires a value.");
                }

                if (!string.Equals(args[index], "blurred-background", StringComparison.OrdinalIgnoreCase))
                {
                    return Failure(
                        $"Unsupported vertical layout '{args[index]}'. Supported value: blurred-background.");
                }

                verticalLayout = VerticalLayoutMode.BlurredBackground;
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
            Destination: inputs.Count > 1 ? inputs[1] : null,
            VerticalLayout: verticalLayout));
    }

    private static CommandParseResult ParseMedia(IReadOnlyList<string> args)
    {
        if (args.Count == 1 || (args.Count == 2 && IsHelp(args[1])))
        {
            return Success(new ParsedCommand(CommandKind.MediaHelp, ShowHelp: true));
        }

        if (!string.Equals(args[1], "init", StringComparison.OrdinalIgnoreCase))
        {
            return Failure($"Unknown media command '{args[1]}'.");
        }

        if (args.Count == 3 && IsHelp(args[2]))
        {
            return Success(new ParsedCommand(CommandKind.MediaInit, ShowHelp: true));
        }

        if (args.Count != 3)
        {
            return Failure("The media init command requires exactly one media root.");
        }

        return Success(new ParsedCommand(CommandKind.MediaInit, Input: args[2]));
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

            if (metadataSubtype is not null
                && metadataType is not (AssetTypes.ShortForm or AssetTypes.Performance))
            {
                return Failure("--subtype is valid only when --type is short-form or performance.");
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

    private static CommandParseResult ParseBuildPlaylist(IReadOnlyList<string> args)
    {
        if (args.Count == 2 && IsHelp(args[1]))
        {
            return Success(new ParsedCommand(CommandKind.BuildPlaylist, ShowHelp: true));
        }

        string? catalogPath = null;
        string? outputPath = null;
        TimeSpan? targetDuration = null;
        int? seed = null;
        string? historyPath = null;
        bool dryRun = false;
        bool optionsEnded = false;
        var inputs = new List<string>();
        for (int index = 1; index < args.Count; index++)
        {
            string argument = args[index];
            if (!optionsEnded && argument == "--")
            {
                optionsEnded = true;
            }
            else if (!optionsEnded && argument == "--dry-run")
            {
                dryRun = true;
            }
            else if (!optionsEnded && argument is "--catalog" or "--output" or "--duration" or "--seed" or "--history")
            {
                if (++index >= args.Count
                    || string.IsNullOrWhiteSpace(args[index])
                    || args[index].StartsWith("-", StringComparison.Ordinal))
                {
                    return Failure($"{argument} requires a value.");
                }

                string value = args[index];
                switch (argument)
                {
                    case "--catalog":
                        catalogPath = value;
                        break;
                    case "--output":
                        outputPath = value;
                        break;
                    case "--duration":
                        if (!TryParseDuration(value, out TimeSpan parsedDuration))
                        {
                            return Failure("--duration must be a positive value such as 6h, 90m, or 3600s.");
                        }

                        targetDuration = parsedDuration;
                        break;
                    case "--seed":
                        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedSeed))
                        {
                            return Failure("--seed must be a 32-bit integer.");
                        }

                        seed = parsedSeed;
                        break;
                    case "--history":
                        historyPath = value;
                        break;
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

        if (inputs.Count != 1)
        {
            return Failure("The build-playlist command requires exactly one library root.");
        }

        if (string.IsNullOrWhiteSpace(catalogPath)
            || string.IsNullOrWhiteSpace(outputPath)
            || targetDuration is null)
        {
            return Failure("The build-playlist command requires --catalog, --output, and --duration.");
        }

        return Success(new ParsedCommand(
            CommandKind.BuildPlaylist,
            inputs[0],
            CatalogPath: catalogPath,
            DryRun: dryRun,
            OutputPath: outputPath,
            TargetDuration: targetDuration,
            Seed: seed,
            HistoryPath: historyPath));
    }

    private static bool TryParseDuration(string value, out TimeSpan duration)
    {
        duration = default;
        if (value.Length < 2
            || !double.TryParse(
                value[..^1],
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double amount)
            || !double.IsFinite(amount)
            || amount <= 0)
        {
            return false;
        }

        double seconds = char.ToLowerInvariant(value[^1]) switch
        {
            'h' => amount * 3600,
            'm' => amount * 60,
            's' => amount,
            _ => -1,
        };
        if (!double.IsFinite(seconds) || seconds <= 0 || seconds > TimeSpan.MaxValue.TotalSeconds)
        {
            return false;
        }

        duration = TimeSpan.FromSeconds(seconds);
        return true;
    }

    private static bool IsHelp(string argument) => argument is "--help" or "-h";

    private static CommandParseResult Success(ParsedCommand command) => new(command, null);

    private static CommandParseResult Failure(string error) => new(null, error);
}
