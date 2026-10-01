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
    Broadcast,
    StationHelp,
    StationValidate,
    StationRun,
    StationStatus,
    StationRollingHelp,
    StationRollingValidate,
    StationRollingRun,
    StationRollingStatus,
    ProgrammingHelp,
    ProgrammingInit,
    ProgrammingValidate,
    ProgrammingStatus,
    ProgrammingCampaignHelp,
    ProgrammingCampaignSet,
    ProgrammingCampaignClear,
    ProgrammingAssetHelp,
    ProgrammingAssetSet,
    ProgrammingAssetReset,
    ProgrammingRollingHelp,
    ProgrammingRollingInit,
    ProgrammingRollingMaintain,
    ProgrammingRollingValidate,
    ProgrammingRollingStatus,
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
    IReadOnlyList<string>? PlaylistPaths = null,
    string? ConfigPath = null,
    string? StatePath = null,
    string? ProgrammingMediaRoot = null,
    string? ProgrammingContentGroupId = null,
    string? ProgrammingAssetId = null,
    bool? ProgrammingDoNotAir = null,
    double? ProgrammingWeightMultiplier = null,
    string? RollingHistoryPath = null,
    int? RollingBaseSeed = null,
    bool AcceptStoppedStaticCutover = false,
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

        if (string.Equals(args[0], "station", StringComparison.OrdinalIgnoreCase))
        {
            return ParseStation(args);
        }

        if (string.Equals(args[0], "programming", StringComparison.OrdinalIgnoreCase))
        {
            return ParseProgramming(args);
        }

        if (string.Equals(args[0], "build-playlist", StringComparison.OrdinalIgnoreCase))
        {
            return ParseBuildPlaylist(args);
        }

        if (string.Equals(args[0], "broadcast", StringComparison.OrdinalIgnoreCase))
        {
            return ParseBroadcast(args);
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

    private static CommandParseResult ParseProgramming(IReadOnlyList<string> args)
    {
        if (args.Count == 1 || (args.Count == 2 && IsHelp(args[1])))
        {
            return Success(new ParsedCommand(CommandKind.ProgrammingHelp, ShowHelp: true));
        }

        if (string.Equals(args[1], "rolling", StringComparison.OrdinalIgnoreCase))
        {
            return ParseProgrammingRolling(args);
        }

        CommandKind kind;
        int optionStart;
        switch (args[1].ToLowerInvariant())
        {
            case "init":
                kind = CommandKind.ProgrammingInit;
                optionStart = 2;
                break;
            case "validate":
                kind = CommandKind.ProgrammingValidate;
                optionStart = 2;
                break;
            case "status":
                kind = CommandKind.ProgrammingStatus;
                optionStart = 2;
                break;
            case "campaign":
                if (args.Count == 2 || (args.Count == 3 && IsHelp(args[2])))
                {
                    return Success(new ParsedCommand(CommandKind.ProgrammingCampaignHelp, ShowHelp: true));
                }

                kind = args[2].ToLowerInvariant() switch
                {
                    "set" => CommandKind.ProgrammingCampaignSet,
                    "clear" => CommandKind.ProgrammingCampaignClear,
                    _ => CommandKind.ProgrammingCampaignHelp,
                };
                if (kind == CommandKind.ProgrammingCampaignHelp)
                {
                    return Failure($"Unknown programming campaign command '{args[2]}'.");
                }

                optionStart = 3;
                break;
            case "asset":
                if (args.Count == 2 || (args.Count == 3 && IsHelp(args[2])))
                {
                    return Success(new ParsedCommand(CommandKind.ProgrammingAssetHelp, ShowHelp: true));
                }

                kind = args[2].ToLowerInvariant() switch
                {
                    "set" => CommandKind.ProgrammingAssetSet,
                    "reset" => CommandKind.ProgrammingAssetReset,
                    _ => CommandKind.ProgrammingAssetHelp,
                };
                if (kind == CommandKind.ProgrammingAssetHelp)
                {
                    return Failure($"Unknown programming asset command '{args[2]}'.");
                }

                optionStart = 3;
                break;
            default:
                return Failure($"Unknown programming command '{args[1]}'.");
        }

        if (args.Count == optionStart + 1 && IsHelp(args[optionStart]))
        {
            return Success(new ParsedCommand(kind, ShowHelp: true));
        }

        string? mediaRoot = null;
        bool? doNotAir = null;
        double? weight = null;
        var positionals = new List<string>();
        for (int index = optionStart; index < args.Count; index++)
        {
            string argument = args[index];
            if (argument is "--media-root" or "--weight" or "--do-not-air")
            {
                if (++index >= args.Count || string.IsNullOrWhiteSpace(args[index]))
                {
                    return Failure($"{argument} requires a value.");
                }

                string value = args[index];
                if (argument == "--media-root")
                {
                    mediaRoot = value;
                }
                else if (argument == "--weight")
                {
                    if (kind is not (CommandKind.ProgrammingCampaignSet or CommandKind.ProgrammingAssetSet))
                    {
                        return Failure("--weight is valid only for programming campaign set or programming asset set.");
                    }

                    if (!double.TryParse(
                            value,
                            NumberStyles.Float,
                            CultureInfo.InvariantCulture,
                            out double parsedWeight)
                        || !double.IsFinite(parsedWeight))
                    {
                        return Failure("--weight requires a finite number.");
                    }

                    weight = parsedWeight;
                }
                else
                {
                    if (kind != CommandKind.ProgrammingAssetSet)
                    {
                        return Failure("--do-not-air is valid only for programming asset set.");
                    }

                    if (!bool.TryParse(value, out bool parsedDoNotAir))
                    {
                        return Failure("--do-not-air requires true or false.");
                    }

                    doNotAir = parsedDoNotAir;
                }
            }
            else if (argument.StartsWith("-", StringComparison.Ordinal))
            {
                return Failure($"Unknown option '{argument}'.");
            }
            else
            {
                positionals.Add(argument);
            }
        }

        if (string.IsNullOrWhiteSpace(mediaRoot))
        {
            return Failure("The programming command requires --media-root <path>.");
        }

        int requiredPositionals = kind is CommandKind.ProgrammingCampaignSet or
            CommandKind.ProgrammingAssetSet or CommandKind.ProgrammingAssetReset
            ? 1
            : 0;
        if (positionals.Count != requiredPositionals)
        {
            string requirement = requiredPositionals == 0
                ? "no positional values"
                : kind == CommandKind.ProgrammingCampaignSet
                    ? "exactly one contentGroupId"
                    : "exactly one assetId";
            return Failure($"The programming command requires {requirement}.");
        }

        if (kind == CommandKind.ProgrammingAssetSet
            && doNotAir is null
            && weight is null)
        {
            return Failure("programming asset set requires --do-not-air, --weight, or both.");
        }

        return Success(new ParsedCommand(
            kind,
            ProgrammingMediaRoot: mediaRoot,
            ProgrammingContentGroupId: kind == CommandKind.ProgrammingCampaignSet
                ? positionals[0]
                : null,
            ProgrammingAssetId: kind is CommandKind.ProgrammingAssetSet or CommandKind.ProgrammingAssetReset
                ? positionals[0]
                : null,
            ProgrammingDoNotAir: doNotAir,
            ProgrammingWeightMultiplier: weight));
    }

    private static CommandParseResult ParseProgrammingRolling(IReadOnlyList<string> args)
    {
        if (args.Count == 2 || (args.Count == 3 && IsHelp(args[2])))
        {
            return Success(new ParsedCommand(CommandKind.ProgrammingRollingHelp, ShowHelp: true));
        }

        CommandKind? kind = args[2].ToLowerInvariant() switch
        {
            "init" => CommandKind.ProgrammingRollingInit,
            "maintain" => CommandKind.ProgrammingRollingMaintain,
            "validate" => CommandKind.ProgrammingRollingValidate,
            "status" => CommandKind.ProgrammingRollingStatus,
            _ => null,
        };
        if (kind is null)
        {
            return Failure($"Unknown programming rolling command '{args[2]}'.");
        }

        if (args.Count == 4 && IsHelp(args[3]))
        {
            return Success(new ParsedCommand(kind.Value, ShowHelp: true));
        }

        string? mediaRoot = null;
        string? historyPath = null;
        int? baseSeed = null;
        for (int index = 3; index < args.Count; index++)
        {
            string argument = args[index];
            if (argument is not ("--media-root" or "--history" or "--base-seed"))
            {
                return Failure($"Unknown option '{argument}' for programming rolling {args[2]}.");
            }

            if (++index >= args.Count || string.IsNullOrWhiteSpace(args[index]))
            {
                return Failure($"{argument} requires a value.");
            }

            string value = args[index];
            switch (argument)
            {
                case "--media-root":
                    if (mediaRoot is not null) return Failure("--media-root may be specified only once.");
                    if (value.StartsWith("-", StringComparison.Ordinal))
                    {
                        return Failure("--media-root requires a path.");
                    }

                    mediaRoot = value;
                    break;
                case "--history":
                    if (kind != CommandKind.ProgrammingRollingInit)
                    {
                        return Failure("--history is valid only for programming rolling init.");
                    }

                    if (historyPath is not null) return Failure("--history may be specified only once.");
                    if (value.StartsWith("-", StringComparison.Ordinal))
                    {
                        return Failure("--history requires a path.");
                    }

                    historyPath = value;
                    break;
                case "--base-seed":
                    if (kind != CommandKind.ProgrammingRollingInit)
                    {
                        return Failure("--base-seed is valid only for programming rolling init.");
                    }

                    if (baseSeed is not null) return Failure("--base-seed may be specified only once.");
                    if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedSeed))
                    {
                        return Failure("--base-seed requires an integer.");
                    }

                    baseSeed = parsedSeed;
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(mediaRoot))
        {
            return Failure("The programming rolling command requires --media-root <path>.");
        }

        return Success(new ParsedCommand(
            kind.Value,
            ProgrammingMediaRoot: mediaRoot,
            RollingHistoryPath: historyPath,
            RollingBaseSeed: baseSeed));
    }

    private static CommandParseResult ParseStation(IReadOnlyList<string> args)
    {
        if (args.Count == 1 || (args.Count == 2 && IsHelp(args[1])))
        {
            return Success(new ParsedCommand(CommandKind.StationHelp, ShowHelp: true));
        }

        if (string.Equals(args[1], "rolling", StringComparison.OrdinalIgnoreCase))
        {
            return ParseStationRolling(args);
        }

        CommandKind? kind = args[1].ToLowerInvariant() switch
        {
            "validate" => CommandKind.StationValidate,
            "run" => CommandKind.StationRun,
            "status" => CommandKind.StationStatus,
            _ => null,
        };
        if (kind is null)
        {
            return Failure($"Unknown station command '{args[1]}'.");
        }

        if (args.Count == 3 && IsHelp(args[2]))
        {
            return Success(new ParsedCommand(kind.Value, ShowHelp: true));
        }

        string? configPath = null;
        string? statePath = null;
        for (int index = 2; index < args.Count; index++)
        {
            string argument = args[index];
            string expectedOption = kind == CommandKind.StationStatus ? "--state" : "--config";
            if (argument != expectedOption)
            {
                return Failure($"Unknown option '{argument}' for station {args[1]}.");
            }

            if (++index >= args.Count
                || string.IsNullOrWhiteSpace(args[index])
                || args[index].StartsWith("-", StringComparison.Ordinal))
            {
                return Failure($"{expectedOption} requires a path.");
            }

            if (kind == CommandKind.StationStatus)
            {
                if (statePath is not null) return Failure("--state may be specified only once.");
                statePath = args[index];
            }
            else
            {
                if (configPath is not null) return Failure("--config may be specified only once.");
                configPath = args[index];
            }
        }

        if (kind is CommandKind.StationValidate or CommandKind.StationRun
            && string.IsNullOrWhiteSpace(configPath))
        {
            return Failure($"The station {args[1]} command requires --config <path>.");
        }

        return Success(new ParsedCommand(
            kind.Value,
            ConfigPath: configPath,
            StatePath: statePath));
    }

    private static CommandParseResult ParseStationRolling(IReadOnlyList<string> args)
    {
        if (args.Count == 2 || (args.Count == 3 && IsHelp(args[2])))
        {
            return Success(new ParsedCommand(CommandKind.StationRollingHelp, ShowHelp: true));
        }

        CommandKind? kind = args[2].ToLowerInvariant() switch
        {
            "validate" => CommandKind.StationRollingValidate,
            "run" => CommandKind.StationRollingRun,
            "status" => CommandKind.StationRollingStatus,
            _ => null,
        };
        if (kind is null)
        {
            return Failure($"Unknown station rolling command '{args[2]}'.");
        }

        if (args.Count == 4 && IsHelp(args[3]))
        {
            return Success(new ParsedCommand(kind.Value, ShowHelp: true));
        }

        string? configPath = null;
        bool acceptStoppedStaticCutover = false;
        for (int index = 3; index < args.Count; index++)
        {
            string argument = args[index];
            if (argument == "--accept-stopped-static-cutover")
            {
                if (kind != CommandKind.StationRollingRun)
                {
                    return Failure(
                        "--accept-stopped-static-cutover is valid only for station rolling run.");
                }

                if (acceptStoppedStaticCutover)
                {
                    return Failure("--accept-stopped-static-cutover may be specified only once.");
                }

                acceptStoppedStaticCutover = true;
                continue;
            }

            if (argument != "--config")
            {
                return Failure($"Unknown option '{argument}' for station rolling {args[2]}.");
            }

            if (++index >= args.Count
                || string.IsNullOrWhiteSpace(args[index])
                || args[index].StartsWith("-", StringComparison.Ordinal))
            {
                return Failure("--config requires a path.");
            }

            if (configPath is not null)
            {
                return Failure("--config may be specified only once.");
            }

            configPath = args[index];
        }

        if (string.IsNullOrWhiteSpace(configPath))
        {
            return Failure($"The station rolling {args[2]} command requires --config <path>.");
        }

        return Success(new ParsedCommand(
            kind.Value,
            ConfigPath: configPath,
            AcceptStoppedStaticCutover: acceptStoppedStaticCutover));
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

    private static CommandParseResult ParseBroadcast(IReadOnlyList<string> args)
    {
        if (args.Count == 2 && IsHelp(args[1]))
        {
            return Success(new ParsedCommand(CommandKind.Broadcast, ShowHelp: true));
        }

        string? libraryRoot = null;
        bool dryRun = false;
        bool optionsEnded = false;
        var playlistPaths = new List<string>();
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
            else if (!optionsEnded && argument == "--library")
            {
                if (++index >= args.Count
                    || string.IsNullOrWhiteSpace(args[index])
                    || args[index].StartsWith("-", StringComparison.Ordinal))
                {
                    return Failure("--library requires a library root.");
                }

                libraryRoot = args[index];
            }
            else if (!optionsEnded && argument.StartsWith("-", StringComparison.Ordinal))
            {
                return Failure($"Unknown option '{argument}'.");
            }
            else
            {
                playlistPaths.Add(argument);
            }
        }

        if (playlistPaths.Count == 0)
        {
            return Failure("The broadcast command requires at least one playlist file.");
        }

        if (string.IsNullOrWhiteSpace(libraryRoot))
        {
            return Failure("The broadcast command requires --library <library-root>.");
        }

        return Success(new ParsedCommand(
            CommandKind.Broadcast,
            Input: libraryRoot,
            DryRun: dryRun,
            PlaylistPaths: playlistPaths));
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
