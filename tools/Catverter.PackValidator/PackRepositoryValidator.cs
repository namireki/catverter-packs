using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using UnitsNet;

namespace Catverter.PackValidator;

public sealed record ValidationResult(IReadOnlyList<string> Errors, int PackCount)
{
    public bool IsValid => Errors.Count == 0;
}

public sealed partial class PackRepositoryValidator
{
    private const string CatalogSchemaPath = "schemas/catverter-catalog-v1.schema.json";
    private const string PackSchemaPath = "../../schemas/catverter-pack-v1.schema.json";

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private static readonly JsonSerializerOptions CatalogJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true,
    };

    public ValidationResult Validate(string repositoryRoot)
    {
        return ValidateInternal(repositoryRoot, validateHashes: true);
    }

    public ValidationResult UpdateCatalog(string repositoryRoot)
    {
        var initialResult = ValidateInternal(repositoryRoot, validateHashes: false);
        if (!initialResult.IsValid)
        {
            return initialResult;
        }

        var root = NormalizeRepositoryRoot(repositoryRoot);
        var catalogPath = Path.Combine(root, "catalog.json");
        var errors = new List<string>();
        var catalog = ParseObject(catalogPath, "catalog.json", errors);
        if (catalog is null || !TryGetArray(catalog, "packs", "catalog.json", errors, out var entries))
        {
            return new ValidationResult(errors, initialResult.PackCount);
        }

        foreach (var (entryNode, index) in entries.Select((node, index) => (node, index)))
        {
            if (entryNode is not JsonObject entry)
            {
                errors.Add($"catalog.json packs[{index}] must be an object.");
                continue;
            }

            var context = $"catalog.json packs[{index}]";
            if (!TryGetString(entry, "file", context, errors, out var file))
            {
                continue;
            }

            if (!TryResolvePackPath(root, file, out var fullPath, out var pathError))
            {
                errors.Add($"{context}.file {pathError}");
                continue;
            }

            entry["sha256"] = ComputeCanonicalSha256(fullPath);
        }

        if (errors.Count > 0)
        {
            return new ValidationResult(errors, initialResult.PackCount);
        }

        var json = catalog.ToJsonString(CatalogJsonOptions).Replace("\r\n", "\n");
        File.WriteAllText(catalogPath, $"{json}\n", new UTF8Encoding(false));

        return ValidateInternal(root, validateHashes: true);
    }

    public static string ComputeCanonicalSha256(string path)
    {
        var text = File.ReadAllText(path, StrictUtf8)
            .Replace("\r\n", "\n")
            .Replace('\r', '\n');
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static ValidationResult ValidateInternal(string repositoryRoot, bool validateHashes)
    {
        var errors = new List<string>();
        string root;

        try
        {
            root = NormalizeRepositoryRoot(repositoryRoot);
        }
        catch (Exception exception) when (
            exception is ArgumentException or IOException or NotSupportedException)
        {
            errors.Add($"Repository root is invalid: {exception.Message}");
            return new ValidationResult(errors, 0);
        }

        if (!Directory.Exists(root))
        {
            errors.Add($"Repository root does not exist: {root}");
            return new ValidationResult(errors, 0);
        }

        ValidateRootPackFiles(root, errors);

        var packFiles = DiscoverPackFiles(root, errors);
        var packsByPath = new Dictionary<string, PackMetadata>(StringComparer.Ordinal);

        foreach (var packFile in packFiles)
        {
            var relativePath = ToRepositoryPath(root, packFile);
            if (!TryResolvePackPath(root, relativePath, out _, out var pathError))
            {
                errors.Add($"{relativePath} {pathError}");
                continue;
            }

            if (ContainsReparsePoint(root, packFile))
            {
                errors.Add($"{relativePath} must not contain symbolic links.");
                continue;
            }

            var pack = ParseObject(packFile, relativePath, errors);
            if (pack is null)
            {
                continue;
            }

            var metadata = ValidatePack(pack, relativePath, errors);
            if (metadata is not null)
            {
                packsByPath[relativePath] = metadata;
            }
        }

        var catalogPath = Path.Combine(root, "catalog.json");
        var catalog = ParseObject(catalogPath, "catalog.json", errors);
        if (catalog is null)
        {
            return new ValidationResult(errors, packFiles.Count);
        }

        var entries = ValidateCatalog(catalog, validateHashes, errors);
        ReconcileCatalog(root, entries, packsByPath, validateHashes, errors);

        return new ValidationResult(errors, packFiles.Count);
    }

    private static string NormalizeRepositoryRoot(string repositoryRoot)
    {
        if (string.IsNullOrWhiteSpace(repositoryRoot))
        {
            throw new ArgumentException("A repository root is required.", nameof(repositoryRoot));
        }

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryRoot));
    }

    private static List<string> DiscoverPackFiles(string root, List<string> errors)
    {
        var packsDirectory = Path.Combine(root, "packs");
        if (!Directory.Exists(packsDirectory))
        {
            errors.Add("The packs directory is missing.");
            return [];
        }

        return Directory
            .EnumerateFiles(packsDirectory, "*.json", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();
    }

    private static void ValidateRootPackFiles(string root, List<string> errors)
    {
        foreach (var path in Directory.EnumerateFiles(root, "*.json", SearchOption.TopDirectoryOnly))
        {
            if (string.Equals(Path.GetFileName(path), "catalog.json", StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                var document = JsonNode.Parse(File.ReadAllText(path, StrictUtf8)) as JsonObject;
                if (document is not null &&
                    document.TryGetPropertyValue("documentType", out var documentTypeNode) &&
                    TryGetStringValue(documentTypeNode, out var documentType) &&
                    documentType == "catverter-pack")
                {
                    errors.Add(
                        $"{Path.GetFileName(path)} is a pack at the repository root; " +
                        "packs must use packs/<author>/<pack-id>.json.");
                }
            }
            catch (Exception exception) when (
                exception is JsonException or DecoderFallbackException)
            {
                errors.Add($"{Path.GetFileName(path)} is not valid UTF-8 JSON: {exception.Message}");
            }
        }
    }

    private static JsonObject? ParseObject(string path, string displayPath, List<string> errors)
    {
        if (!File.Exists(path))
        {
            errors.Add($"{displayPath} does not exist.");
            return null;
        }

        try
        {
            var node = JsonNode.Parse(
                File.ReadAllText(path, StrictUtf8),
                documentOptions: new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                });

            if (node is JsonObject result)
            {
                return result;
            }

            errors.Add($"{displayPath} must contain a JSON object.");
        }
        catch (Exception exception) when (
            exception is JsonException or DecoderFallbackException)
        {
            errors.Add($"{displayPath} is not valid UTF-8 JSON: {exception.Message}");
        }

        return null;
    }

    private static PackMetadata? ValidatePack(
        JsonObject pack,
        string relativePath,
        List<string> errors)
    {
        ValidateObject(
            pack,
            relativePath,
            [
                "$schema", "documentType", "schemaVersion", "packageId", "id", "name",
                "version", "minimumAppVersion", "description", "metadata", "modifiers",
                "conversions",
            ],
            [
                "$schema", "documentType", "schemaVersion", "packageId", "id", "name",
                "version", "minimumAppVersion", "description", "conversions",
            ],
            errors);

        ValidateExactString(pack, "$schema", PackSchemaPath, relativePath, errors);
        ValidateExactString(pack, "documentType", "catverter-pack", relativePath, errors);
        ValidateExactInteger(pack, "schemaVersion", 1, relativePath, errors);

        TryGetString(pack, "packageId", relativePath, errors, out var packageId);
        TryGetString(pack, "id", relativePath, errors, out var id);
        TryGetString(pack, "version", relativePath, errors, out var version);

        ValidatePattern(packageId, PackageIdRegex(), $"{relativePath}.packageId", errors);
        ValidatePattern(id, IdRegex(), $"{relativePath}.id", errors);
        ValidatePattern(version, VersionRegex(), $"{relativePath}.version", errors);
        ValidateString(pack, "name", relativePath, errors);
        ValidateString(pack, "description", relativePath, errors);
        ValidateVersion(pack, "minimumAppVersion", relativePath, errors);

        var fileId = Path.GetFileNameWithoutExtension(relativePath);
        if (!string.IsNullOrEmpty(id) &&
            !string.Equals(fileId, id, StringComparison.Ordinal))
        {
            errors.Add(
                $"{relativePath}.id '{id}' must match the file name '{fileId}'.");
        }

        if (pack.TryGetPropertyValue("metadata", out var metadataNode))
        {
            if (metadataNode is JsonObject metadata)
            {
                ValidateMetadata(metadata, $"{relativePath}.metadata", errors);
            }
            else
            {
                errors.Add($"{relativePath}.metadata must be an object.");
            }
        }

        var modifierIds = ValidateModifiers(pack, relativePath, errors);
        ValidateConversions(pack, relativePath, modifierIds, errors);

        if (string.IsNullOrEmpty(packageId) ||
            string.IsNullOrEmpty(id) ||
            string.IsNullOrEmpty(version))
        {
            return null;
        }

        return new PackMetadata(packageId, id, version);
    }

    private static void ValidateMetadata(
        JsonObject metadata,
        string context,
        List<string> errors)
    {
        ValidateObject(
            metadata,
            context,
            ["authors", "license", "homepage", "repository", "tags", "accentColor", "iconUrl"],
            [],
            errors);

        if (metadata.TryGetPropertyValue("authors", out var authorsNode))
        {
            ValidateUniqueStringArray(
                authorsNode,
                $"{context}.authors",
                value => !string.IsNullOrWhiteSpace(value),
                "a non-empty string",
                errors);
        }

        if (metadata.TryGetPropertyValue("tags", out var tagsNode))
        {
            ValidateUniqueStringArray(
                tagsNode,
                $"{context}.tags",
                value => IdRegex().IsMatch(value),
                "a lowercase ID",
                errors);
        }

        ValidateOptionalNonEmptyString(metadata, "license", context, errors);
        ValidateOptionalHttpsUrl(metadata, "homepage", context, errors);
        ValidateOptionalHttpsUrl(metadata, "repository", context, errors);
        ValidateOptionalHttpsUrl(metadata, "iconUrl", context, errors);

        if (metadata.TryGetPropertyValue("accentColor", out var colorNode) &&
            (!TryGetStringValue(colorNode, out var color) || !ColorRegex().IsMatch(color)))
        {
            errors.Add($"{context}.accentColor must be a six-digit hexadecimal color.");
        }
    }

    private static HashSet<string> ValidateModifiers(
        JsonObject pack,
        string relativePath,
        List<string> errors)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var incompatibleReferences = new List<(string Context, string ModifierId, string Reference)>();

        if (!pack.TryGetPropertyValue("modifiers", out var modifiersNode))
        {
            return ids;
        }

        if (modifiersNode is not JsonArray modifiers)
        {
            errors.Add($"{relativePath}.modifiers must be an array.");
            return ids;
        }

        foreach (var (node, index) in modifiers.Select((node, index) => (node, index)))
        {
            var context = $"{relativePath}.modifiers[{index}]";
            if (node is not JsonObject modifier)
            {
                errors.Add($"{context} must be an object.");
                continue;
            }

            ValidateObject(
                modifier,
                context,
                [
                    "id", "name", "multiplier", "quantities", "aspects",
                    "combinationGroup", "incompatibleWith",
                ],
                ["id", "name", "multiplier", "combinationGroup"],
                errors);

            TryGetString(modifier, "id", context, errors, out var id);
            ValidatePattern(id, IdRegex(), $"{context}.id", errors);
            ValidateString(modifier, "name", context, errors);
            ValidatePositiveNumber(modifier, "multiplier", context, errors);
            ValidateId(modifier, "combinationGroup", context, errors);

            if (!string.IsNullOrEmpty(id) && !ids.Add(id))
            {
                errors.Add($"{context}.id '{id}' is duplicated.");
            }

            if (modifier.TryGetPropertyValue("quantities", out var quantitiesNode))
            {
                foreach (var quantity in ValidateUniqueStringArray(
                             quantitiesNode,
                             $"{context}.quantities",
                             value => !string.IsNullOrWhiteSpace(value),
                             "a non-empty string",
                             errors))
                {
                    if (!IsKnownQuantity(quantity))
                    {
                        errors.Add(
                            $"{context}.quantities contains unknown UnitsNet quantity " +
                            $"'{quantity}'.");
                    }
                }
            }

            if (modifier.TryGetPropertyValue("aspects", out var aspectsNode))
            {
                ValidateUniqueStringArray(
                    aspectsNode,
                    $"{context}.aspects",
                    value => IdRegex().IsMatch(value),
                    "a lowercase ID",
                    errors);
            }

            if (modifier.TryGetPropertyValue("incompatibleWith", out var incompatibleNode))
            {
                foreach (var reference in ValidateUniqueStringArray(
                             incompatibleNode,
                             $"{context}.incompatibleWith",
                             value => IdRegex().IsMatch(value),
                             "a lowercase ID",
                             errors))
                {
                    incompatibleReferences.Add((context, id, reference));
                }
            }
        }

        foreach (var reference in incompatibleReferences)
        {
            if (!ids.Contains(reference.Reference))
            {
                errors.Add(
                    $"{reference.Context}.incompatibleWith references unknown modifier " +
                    $"'{reference.Reference}'.");
            }

            if (string.Equals(reference.ModifierId, reference.Reference, StringComparison.Ordinal))
            {
                errors.Add(
                    $"{reference.Context}.incompatibleWith must not reference itself.");
            }
        }

        return ids;
    }

    private static void ValidateConversions(
        JsonObject pack,
        string relativePath,
        HashSet<string> modifierIds,
        List<string> errors)
    {
        if (!TryGetArray(pack, "conversions", relativePath, errors, out var conversions))
        {
            return;
        }

        if (conversions.Count == 0)
        {
            errors.Add($"{relativePath}.conversions must contain at least one conversion.");
        }

        var conversionIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (node, index) in conversions.Select((node, index) => (node, index)))
        {
            var context = $"{relativePath}.conversions[{index}]";
            if (node is not JsonObject conversion)
            {
                errors.Add($"{context} must be an object.");
                continue;
            }

            ValidateObject(
                conversion,
                context,
                [
                    "id", "singularName", "pluralName", "quantity", "unit", "value",
                    "aspect", "modifierIds", "sourceUrl", "notes",
                ],
                ["id", "singularName", "pluralName", "quantity", "unit", "value"],
                errors);

            TryGetString(conversion, "id", context, errors, out var id);
            ValidatePattern(id, IdRegex(), $"{context}.id", errors);
            ValidateString(conversion, "singularName", context, errors);
            ValidateString(conversion, "pluralName", context, errors);
            ValidatePositiveNumber(conversion, "value", context, errors);
            ValidateOptionalId(conversion, "aspect", context, errors);
            ValidateOptionalHttpsUrl(conversion, "sourceUrl", context, errors);

            if (!string.IsNullOrEmpty(id) && !conversionIds.Add(id))
            {
                errors.Add($"{context}.id '{id}' is duplicated.");
            }

            TryGetString(conversion, "quantity", context, errors, out var quantity);
            TryGetString(conversion, "unit", context, errors, out var unit);
            ValidateQuantityAndUnit(quantity, unit, context, errors);

            if (conversion.TryGetPropertyValue("modifierIds", out var modifierIdsNode))
            {
                foreach (var modifierId in ValidateUniqueStringArray(
                             modifierIdsNode,
                             $"{context}.modifierIds",
                             value => IdRegex().IsMatch(value),
                             "a lowercase ID",
                             errors))
                {
                    if (!modifierIds.Contains(modifierId))
                    {
                        errors.Add(
                            $"{context}.modifierIds references unknown modifier '{modifierId}'.");
                    }
                }
            }

            if (conversion.TryGetPropertyValue("notes", out var notesNode))
            {
                if (!TryGetStringValue(notesNode, out var notes))
                {
                    errors.Add($"{context}.notes must be a string.");
                }
                else if (notes.Length > 1000)
                {
                    errors.Add($"{context}.notes must not exceed 1000 characters.");
                }
            }
        }
    }

    private static void ValidateQuantityAndUnit(
        string quantity,
        string unit,
        string context,
        List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(quantity))
        {
            errors.Add($"{context}.quantity must be a non-empty string.");
        }

        if (string.IsNullOrWhiteSpace(unit))
        {
            errors.Add($"{context}.unit must be a non-empty string.");
        }

        if (string.IsNullOrWhiteSpace(quantity) || string.IsNullOrWhiteSpace(unit))
        {
            return;
        }

        var quantityInfo = Quantity.Infos.FirstOrDefault(
            info => string.Equals(info.Name, quantity, StringComparison.Ordinal));
        if (quantityInfo is null)
        {
            errors.Add($"{context}.quantity '{quantity}' is not an invariant UnitsNet quantity.");
            return;
        }

        if (!quantityInfo.UnitInfos.Any(
                info => string.Equals(info.Name, unit, StringComparison.Ordinal)))
        {
            errors.Add(
                $"{context}.unit '{unit}' is not valid for UnitsNet quantity '{quantity}'.");
        }
    }

    private static bool IsKnownQuantity(string quantity)
    {
        return Quantity.Infos.Any(
            info => string.Equals(info.Name, quantity, StringComparison.Ordinal));
    }

    private static IReadOnlyList<CatalogEntry> ValidateCatalog(
        JsonObject catalog,
        bool validateHashes,
        List<string> errors)
    {
        const string context = "catalog.json";
        ValidateObject(
            catalog,
            context,
            [
                "$schema", "documentType", "schemaVersion", "repositoryId", "version",
                "minimumAppVersion", "packs",
            ],
            [
                "$schema", "documentType", "schemaVersion", "repositoryId", "version",
                "minimumAppVersion", "packs",
            ],
            errors);

        ValidateExactString(catalog, "$schema", CatalogSchemaPath, context, errors);
        ValidateExactString(catalog, "documentType", "catverter-catalog", context, errors);
        ValidateExactInteger(catalog, "schemaVersion", 1, context, errors);
        ValidatePackageId(catalog, "repositoryId", context, errors);
        ValidateVersion(catalog, "version", context, errors);
        ValidateVersion(catalog, "minimumAppVersion", context, errors);

        if (!TryGetArray(catalog, "packs", context, errors, out var packs))
        {
            return [];
        }

        if (packs.Count == 0)
        {
            errors.Add("catalog.json.packs must contain at least one entry.");
        }

        var entries = new List<CatalogEntry>();
        var packageIds = new HashSet<string>(StringComparer.Ordinal);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var files = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (node, index) in packs.Select((node, index) => (node, index)))
        {
            var entryContext = $"{context}.packs[{index}]";
            if (node is not JsonObject entry)
            {
                errors.Add($"{entryContext} must be an object.");
                continue;
            }

            var required = validateHashes
                ? new[] { "packageId", "id", "version", "file", "sha256" }
                : new[] { "packageId", "id", "version", "file" };
            ValidateObject(
                entry,
                entryContext,
                ["packageId", "id", "version", "file", "sha256"],
                required,
                errors);

            TryGetString(entry, "packageId", entryContext, errors, out var packageId);
            TryGetString(entry, "id", entryContext, errors, out var id);
            TryGetString(entry, "version", entryContext, errors, out var version);
            TryGetString(entry, "file", entryContext, errors, out var file);
            TryGetString(entry, "sha256", entryContext, errors, out var sha256);

            ValidatePattern(packageId, PackageIdRegex(), $"{entryContext}.packageId", errors);
            ValidatePattern(id, IdRegex(), $"{entryContext}.id", errors);
            ValidatePattern(version, VersionRegex(), $"{entryContext}.version", errors);

            if (!TryValidatePackRepositoryPath(file, out var pathError))
            {
                errors.Add($"{entryContext}.file {pathError}");
            }

            if (validateHashes)
            {
                ValidatePattern(sha256, Sha256Regex(), $"{entryContext}.sha256", errors);
            }

            ValidateUnique(packageIds, packageId, $"{entryContext}.packageId", errors);
            ValidateUnique(ids, id, $"{entryContext}.id", errors);
            ValidateUnique(files, file, $"{entryContext}.file", errors);

            entries.Add(new CatalogEntry(packageId, id, version, file, sha256, entryContext));
        }

        return entries;
    }

    private static void ReconcileCatalog(
        string root,
        IReadOnlyList<CatalogEntry> entries,
        IReadOnlyDictionary<string, PackMetadata> packsByPath,
        bool validateHashes,
        List<string> errors)
    {
        var catalogFiles = new HashSet<string>(
            entries.Select(entry => entry.File),
            StringComparer.Ordinal);

        foreach (var packPath in packsByPath.Keys)
        {
            if (!catalogFiles.Contains(packPath))
            {
                errors.Add($"{packPath} is not listed in catalog.json.");
            }
        }

        foreach (var entry in entries)
        {
            if (!TryResolvePackPath(root, entry.File, out var fullPath, out _))
            {
                continue;
            }

            if (!File.Exists(fullPath))
            {
                errors.Add($"{entry.Context}.file '{entry.File}' does not exist.");
                continue;
            }

            if (ContainsReparsePoint(root, fullPath))
            {
                errors.Add($"{entry.Context}.file '{entry.File}' must not contain symbolic links.");
                continue;
            }

            if (!packsByPath.TryGetValue(entry.File, out var pack))
            {
                continue;
            }

            CompareCatalogMetadata(
                entry.Context,
                "packageId",
                entry.PackageId,
                pack.PackageId,
                errors);
            CompareCatalogMetadata(entry.Context, "id", entry.Id, pack.Id, errors);
            CompareCatalogMetadata(entry.Context, "version", entry.Version, pack.Version, errors);

            if (validateHashes)
            {
                var actualHash = ComputeCanonicalSha256(fullPath);
                if (!string.Equals(entry.Sha256, actualHash, StringComparison.Ordinal))
                {
                    errors.Add(
                        $"{entry.Context}.sha256 does not match {entry.File}; " +
                        $"expected '{actualHash}'.");
                }
            }
        }
    }

    private static void CompareCatalogMetadata(
        string context,
        string property,
        string catalogValue,
        string packValue,
        List<string> errors)
    {
        if (!string.IsNullOrEmpty(catalogValue) &&
            !string.IsNullOrEmpty(packValue) &&
            !string.Equals(catalogValue, packValue, StringComparison.Ordinal))
        {
            errors.Add(
                $"{context}.{property} '{catalogValue}' does not match pack value " +
                $"'{packValue}'.");
        }
    }

    private static bool TryResolvePackPath(
        string root,
        string relativePath,
        out string fullPath,
        out string error)
    {
        fullPath = string.Empty;
        if (!TryValidatePackRepositoryPath(relativePath, out error))
        {
            return false;
        }

        var segments = relativePath.Split('/');
        fullPath = Path.GetFullPath(Path.Combine(root, segments[0], segments[1], segments[2]));
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var rootPrefix = $"{Path.TrimEndingDirectorySeparator(root)}{Path.DirectorySeparatorChar}";

        if (!fullPath.StartsWith(rootPrefix, comparison))
        {
            error = $"'{relativePath}' resolves outside the repository.";
            fullPath = string.Empty;
            return false;
        }

        return true;
    }

    private static bool TryValidatePackRepositoryPath(string path, out string error)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            error = "must be a non-empty repository-relative path.";
            return false;
        }

        if (path.Contains('\\'))
        {
            error = $"'{path}' must use forward slashes.";
            return false;
        }

        if (Path.IsPathRooted(path) ||
            path.StartsWith("/", StringComparison.Ordinal) ||
            WindowsDrivePathRegex().IsMatch(path))
        {
            error = $"'{path}' must not be rooted.";
            return false;
        }

        var segments = path.Split('/');
        if (segments.Any(segment => segment is "." or ".."))
        {
            error = $"'{path}' must not contain traversal segments.";
            return false;
        }

        if (segments.Length != 3 ||
            !string.Equals(segments[0], "packs", StringComparison.Ordinal))
        {
            error = $"'{path}' must use packs/<author>/<pack-id>.json.";
            return false;
        }

        if (!IdRegex().IsMatch(segments[1]))
        {
            error = $"'{path}' has an invalid author folder; use a lowercase slug.";
            return false;
        }

        var fileName = segments[2];
        if (!fileName.EndsWith(".json", StringComparison.Ordinal) ||
            !IdRegex().IsMatch(fileName[..^5]))
        {
            error = $"'{path}' has an invalid pack file name; use <pack-id>.json.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static bool ContainsReparsePoint(string root, string fullPath)
    {
        var relativePath = Path.GetRelativePath(root, fullPath);
        var currentPath = root;

        foreach (var segment in relativePath.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            currentPath = Path.Combine(currentPath, segment);
            if ((File.GetAttributes(currentPath) & FileAttributes.ReparsePoint) != 0)
            {
                return true;
            }
        }

        return false;
    }

    private static string ToRepositoryPath(string root, string fullPath)
    {
        return Path.GetRelativePath(root, fullPath).Replace('\\', '/');
    }

    private static void ValidateObject(
        JsonObject value,
        string context,
        IEnumerable<string> allowedProperties,
        IEnumerable<string> requiredProperties,
        List<string> errors)
    {
        var allowed = allowedProperties.ToHashSet(StringComparer.Ordinal);
        foreach (var property in value)
        {
            if (!allowed.Contains(property.Key))
            {
                errors.Add($"{context} contains unsupported property '{property.Key}'.");
            }
        }

        foreach (var property in requiredProperties)
        {
            if (!value.TryGetPropertyValue(property, out var node) || node is null)
            {
                errors.Add($"{context} is missing required property '{property}'.");
            }
        }
    }

    private static void ValidateExactString(
        JsonObject value,
        string property,
        string expected,
        string context,
        List<string> errors)
    {
        if (TryGetString(value, property, context, errors, out var actual) &&
            !string.Equals(actual, expected, StringComparison.Ordinal))
        {
            errors.Add($"{context}.{property} must be '{expected}'.");
        }
    }

    private static void ValidateExactInteger(
        JsonObject value,
        string property,
        int expected,
        string context,
        List<string> errors)
    {
        if (!value.TryGetPropertyValue(property, out var node) || node is null)
        {
            return;
        }

        if (node is not JsonValue jsonValue ||
            !jsonValue.TryGetValue<int>(out var actual))
        {
            errors.Add($"{context}.{property} must be an integer.");
        }
        else if (actual != expected)
        {
            errors.Add($"{context}.{property} must be {expected}.");
        }
    }

    private static void ValidateString(
        JsonObject value,
        string property,
        string context,
        List<string> errors)
    {
        if (TryGetString(value, property, context, errors, out var text) &&
            string.IsNullOrWhiteSpace(text))
        {
            errors.Add($"{context}.{property} must be a non-empty string.");
        }
    }

    private static void ValidateOptionalNonEmptyString(
        JsonObject value,
        string property,
        string context,
        List<string> errors)
    {
        if (value.ContainsKey(property))
        {
            ValidateString(value, property, context, errors);
        }
    }

    private static void ValidatePackageId(
        JsonObject value,
        string property,
        string context,
        List<string> errors)
    {
        if (TryGetString(value, property, context, errors, out var text))
        {
            ValidatePattern(text, PackageIdRegex(), $"{context}.{property}", errors);
        }
    }

    private static void ValidateVersion(
        JsonObject value,
        string property,
        string context,
        List<string> errors)
    {
        if (TryGetString(value, property, context, errors, out var text))
        {
            ValidatePattern(text, VersionRegex(), $"{context}.{property}", errors);
        }
    }

    private static void ValidateId(
        JsonObject value,
        string property,
        string context,
        List<string> errors)
    {
        if (TryGetString(value, property, context, errors, out var text))
        {
            ValidatePattern(text, IdRegex(), $"{context}.{property}", errors);
        }
    }

    private static void ValidateOptionalId(
        JsonObject value,
        string property,
        string context,
        List<string> errors)
    {
        if (value.ContainsKey(property))
        {
            ValidateId(value, property, context, errors);
        }
    }

    private static void ValidatePositiveNumber(
        JsonObject value,
        string property,
        string context,
        List<string> errors)
    {
        if (!value.TryGetPropertyValue(property, out var node) || node is null)
        {
            return;
        }

        if (node is not JsonValue jsonValue ||
            !jsonValue.TryGetValue<double>(out var number) ||
            !double.IsFinite(number))
        {
            errors.Add($"{context}.{property} must be a finite number.");
        }
        else if (number <= 0)
        {
            errors.Add($"{context}.{property} must be greater than zero.");
        }
    }

    private static void ValidateOptionalHttpsUrl(
        JsonObject value,
        string property,
        string context,
        List<string> errors)
    {
        if (!value.TryGetPropertyValue(property, out var node))
        {
            return;
        }

        if (!TryGetStringValue(node, out var text) ||
            !Uri.TryCreate(text, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            errors.Add($"{context}.{property} must be an absolute HTTPS URL.");
        }
    }

    private static IReadOnlyList<string> ValidateUniqueStringArray(
        JsonNode? node,
        string context,
        Func<string, bool> isValid,
        string expectedDescription,
        List<string> errors)
    {
        if (node is not JsonArray array)
        {
            errors.Add($"{context} must be an array.");
            return [];
        }

        var values = new List<string>();
        var uniqueValues = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (item, index) in array.Select((item, index) => (item, index)))
        {
            if (!TryGetStringValue(item, out var text) || !isValid(text))
            {
                errors.Add($"{context}[{index}] must be {expectedDescription}.");
                continue;
            }

            if (!uniqueValues.Add(text))
            {
                errors.Add($"{context} contains duplicate value '{text}'.");
            }

            values.Add(text);
        }

        return values;
    }

    private static void ValidatePattern(
        string value,
        Regex regex,
        string context,
        List<string> errors)
    {
        if (!regex.IsMatch(value))
        {
            errors.Add($"{context} has an invalid value '{value}'.");
        }
    }

    private static void ValidateUnique(
        HashSet<string> values,
        string value,
        string context,
        List<string> errors)
    {
        if (!string.IsNullOrEmpty(value) && !values.Add(value))
        {
            errors.Add($"{context} duplicates '{value}'.");
        }
    }

    private static bool TryGetString(
        JsonObject value,
        string property,
        string context,
        List<string> errors,
        out string result)
    {
        result = string.Empty;
        if (!value.TryGetPropertyValue(property, out var node) || node is null)
        {
            return false;
        }

        if (TryGetStringValue(node, out result))
        {
            return true;
        }

        errors.Add($"{context}.{property} must be a string.");
        return false;
    }

    private static bool TryGetStringValue(JsonNode? node, out string result)
    {
        result = string.Empty;
        if (node is JsonValue value &&
            value.TryGetValue<string>(out var text) &&
            text is not null)
        {
            result = text;
            return true;
        }

        return false;
    }

    private static bool TryGetArray(
        JsonObject value,
        string property,
        string context,
        List<string> errors,
        out JsonArray result)
    {
        result = [];
        if (!value.TryGetPropertyValue(property, out var node) || node is null)
        {
            return false;
        }

        if (node is JsonArray array)
        {
            result = array;
            return true;
        }

        errors.Add($"{context}.{property} must be an array.");
        return false;
    }

    [GeneratedRegex("^[a-z0-9](?:[a-z0-9-]*[a-z0-9])?$", RegexOptions.CultureInvariant)]
    private static partial Regex IdRegex();

    [GeneratedRegex(
        "^[a-z0-9](?:[a-z0-9-]*[a-z0-9])?(?:\\.[a-z0-9](?:[a-z0-9-]*[a-z0-9])?){2,}$",
        RegexOptions.CultureInvariant)]
    private static partial Regex PackageIdRegex();

    [GeneratedRegex("^[0-9]+(?:\\.[0-9]+){1,3}$", RegexOptions.CultureInvariant)]
    private static partial Regex VersionRegex();

    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Regex();

    [GeneratedRegex("^#[0-9A-Fa-f]{6}$", RegexOptions.CultureInvariant)]
    private static partial Regex ColorRegex();

    [GeneratedRegex("^[A-Za-z]:[/\\\\]", RegexOptions.CultureInvariant)]
    private static partial Regex WindowsDrivePathRegex();

    private sealed record PackMetadata(string PackageId, string Id, string Version);

    private sealed record CatalogEntry(
        string PackageId,
        string Id,
        string Version,
        string File,
        string Sha256,
        string Context);
}
