using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using UnitsNet;

namespace Catverter.PackValidator;

public sealed record ValidationResult(
    IReadOnlyList<string> Errors,
    int PackCount,
    int OfficialPackCount = 0,
    int ContributedPackCount = 0,
    int LocaleCount = 0)
{
    public bool IsValid => Errors.Count == 0;
}

public sealed partial class PackRepositoryValidator
{
    private const string CatalogSchemaPath = "schemas/catverter-catalog-v1.schema.json";
    private const string LocaleSchemaPath = "../schemas/catverter-locale-v1.schema.json";
    private const string PackSchemaPath = "../../schemas/catverter-pack-v1.schema.json";
    private const string CatalogContentBaseUrl =
        "https://raw.githubusercontent.com/namireki/catverter-packs/{revision}/";
    private const string OfficialAuthor = "Namireki Studio";
    private const string OfficialLicense = "Apache-2.0";
    private const string OfficialMaintainer = "namireki";
    private const string OfficialPackageIdPrefix = "com.namirekistudio.catverter.pack.";
    private const string SourceCatalogFile = "packs/catalog.json";
    private const string SourceCatalogRepositoryId = "com.namirekistudio.catverter.packs";
    private const string SourceRepositoryUrl = "https://github.com/namireki/CatverterV2";

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
        if (catalog is null ||
            !TryGetArray(catalog, "packs", "catalog.json", errors, out var entries) ||
            !TryGetArray(catalog, "locales", "catalog.json", errors, out var localeEntries))
        {
            return new ValidationResult(
                errors,
                initialResult.PackCount,
                initialResult.OfficialPackCount,
                initialResult.ContributedPackCount,
                initialResult.LocaleCount);
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

        foreach (var (entryNode, index) in localeEntries.Select((node, index) => (node, index)))
        {
            if (entryNode is not JsonObject entry)
            {
                errors.Add($"catalog.json locales[{index}] must be an object.");
                continue;
            }

            var context = $"catalog.json locales[{index}]";
            if (!TryGetString(entry, "file", context, errors, out var file))
            {
                continue;
            }

            if (!TryResolveLocalePath(root, file, out var fullPath, out var pathError))
            {
                errors.Add($"{context}.file {pathError}");
                continue;
            }

            entry["sha256"] = ComputeCanonicalSha256(fullPath);
        }

        if (errors.Count > 0)
        {
            return new ValidationResult(
                errors,
                initialResult.PackCount,
                initialResult.OfficialPackCount,
                initialResult.ContributedPackCount,
                initialResult.LocaleCount);
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

        var localeFiles = DiscoverLocaleFiles(root, errors);
        var localesByPath = new Dictionary<string, LocaleMetadata>(StringComparer.Ordinal);
        foreach (var localeFile in localeFiles)
        {
            var relativePath = ToRepositoryPath(root, localeFile);
            if (!TryResolveLocalePath(root, relativePath, out _, out var pathError))
            {
                errors.Add($"{relativePath} {pathError}");
                continue;
            }

            if (ContainsReparsePoint(root, localeFile))
            {
                errors.Add($"{relativePath} must not contain symbolic links.");
                continue;
            }

            var locale = ParseObject(localeFile, relativePath, errors);
            if (locale is null)
            {
                continue;
            }

            var metadata = ValidateLocale(locale, relativePath, errors);
            if (metadata is not null)
            {
                localesByPath[relativePath] = metadata;
            }
        }

        var catalogPath = Path.Combine(root, "catalog.json");
        var catalog = ParseObject(catalogPath, "catalog.json", errors);
        if (catalog is null)
        {
            return new ValidationResult(errors, packFiles.Count);
        }

        var entries = ValidateCatalog(
            catalog,
            validateHashes,
            errors,
            out var localeEntries,
            out var sourceCatalog);
        ReconcileCatalog(
            root,
            entries,
            packsByPath,
            sourceCatalog,
            validateHashes,
            errors);
        ReconcileLocales(
            root,
            localeEntries,
            localesByPath,
            packsByPath,
            entries,
            sourceCatalog,
            validateHashes,
            errors);

        return new ValidationResult(
            errors,
            packFiles.Count,
            entries.Count(entry => entry.Classification == "official"),
            entries.Count(entry => entry.Classification == "contributed"),
            localeFiles.Count);
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

    private static List<string> DiscoverLocaleFiles(string root, List<string> errors)
    {
        var localesDirectory = Path.Combine(root, "locales");
        if (!Directory.Exists(localesDirectory))
        {
            errors.Add("The locales directory is missing.");
            return [];
        }

        return Directory
            .EnumerateFiles(localesDirectory, "*.json", SearchOption.AllDirectories)
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

        if (!string.IsNullOrEmpty(packageId) &&
            !string.IsNullOrEmpty(id) &&
            !packageId.EndsWith($".{id}", StringComparison.Ordinal))
        {
            errors.Add(
                $"{relativePath}.packageId '{packageId}' must end with '.{id}'.");
        }

        var ownership = PackOwnership.Empty;
        if (pack.TryGetPropertyValue("metadata", out var metadataNode))
        {
            if (metadataNode is JsonObject metadata)
            {
                ownership = ValidateMetadata(metadata, $"{relativePath}.metadata", errors);
            }
            else
            {
                errors.Add($"{relativePath}.metadata must be an object.");
            }
        }

        var modifierIds = ValidateModifiers(pack, relativePath, errors);
        var conversionIds = ValidateConversions(pack, relativePath, modifierIds, errors);

        if (string.IsNullOrEmpty(packageId) ||
            string.IsNullOrEmpty(id) ||
            string.IsNullOrEmpty(version))
        {
            return null;
        }

        return new PackMetadata(
            packageId,
            id,
            version,
            ownership,
            modifierIds,
            conversionIds);
    }

    private static PackOwnership ValidateMetadata(
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

        IReadOnlyList<string> authors = [];
        if (metadata.TryGetPropertyValue("authors", out var authorsNode))
        {
            authors = ValidateUniqueStringArray(
                authorsNode,
                $"{context}.authors",
                value => !string.IsNullOrWhiteSpace(value),
                "a non-empty string",
                errors,
                StringComparer.OrdinalIgnoreCase);
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

        TryGetOptionalString(metadata, "license", out var license);
        TryGetOptionalString(metadata, "repository", out var repository);
        return new PackOwnership(authors, license, repository);
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

    private static HashSet<string> ValidateConversions(
        JsonObject pack,
        string relativePath,
        HashSet<string> modifierIds,
        List<string> errors)
    {
        var conversionIds = new HashSet<string>(StringComparer.Ordinal);
        if (!TryGetArray(pack, "conversions", relativePath, errors, out var conversions))
        {
            return conversionIds;
        }

        if (conversions.Count == 0)
        {
            errors.Add($"{relativePath}.conversions must contain at least one conversion.");
        }

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

        return conversionIds;
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
        List<string> errors,
        out IReadOnlyList<LocaleCatalogEntry> localeEntries,
        out SourceCatalogMetadata sourceCatalog)
    {
        localeEntries = [];
        sourceCatalog = SourceCatalogMetadata.Empty;
        const string context = "catalog.json";
        ValidateObject(
            catalog,
            context,
            [
                "$schema", "documentType", "schemaVersion", "repositoryId", "version",
                "minimumAppVersion", "contentBaseUrl", "sourceCatalog", "packs",
                "locales",
            ],
            [
                "$schema", "documentType", "schemaVersion", "repositoryId", "version",
                "minimumAppVersion", "contentBaseUrl", "sourceCatalog", "packs",
                "locales",
            ],
            errors);

        ValidateExactString(catalog, "$schema", CatalogSchemaPath, context, errors);
        ValidateExactString(catalog, "documentType", "catverter-catalog", context, errors);
        ValidateExactInteger(catalog, "schemaVersion", 1, context, errors);
        ValidatePackageId(catalog, "repositoryId", context, errors);
        ValidateVersion(catalog, "version", context, errors);
        ValidateVersion(catalog, "minimumAppVersion", context, errors);
        ValidateExactString(
            catalog,
            "contentBaseUrl",
            CatalogContentBaseUrl,
            context,
            errors);

        if (catalog.TryGetPropertyValue("sourceCatalog", out var sourceCatalogNode))
        {
            if (sourceCatalogNode is JsonObject sourceCatalogObject)
            {
                sourceCatalog = ValidateSourceCatalog(
                    sourceCatalogObject,
                    $"{context}.sourceCatalog",
                    errors);
            }
            else
            {
                errors.Add($"{context}.sourceCatalog must be an object.");
            }
        }

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
                ? new[]
                {
                    "packageId", "id", "version", "classification", "maintainer",
                    "file", "sha256",
                }
                : new[]
                {
                    "packageId", "id", "version", "classification", "maintainer",
                    "file",
                };
            ValidateObject(
                entry,
                entryContext,
                [
                    "packageId", "id", "version", "classification", "maintainer",
                    "file", "sha256", "source",
                ],
                required,
                errors);

            TryGetString(entry, "packageId", entryContext, errors, out var packageId);
            TryGetString(entry, "id", entryContext, errors, out var id);
            TryGetString(entry, "version", entryContext, errors, out var version);
            TryGetString(
                entry,
                "classification",
                entryContext,
                errors,
                out var classification);
            TryGetString(entry, "maintainer", entryContext, errors, out var maintainer);
            TryGetString(entry, "file", entryContext, errors, out var file);
            TryGetString(entry, "sha256", entryContext, errors, out var sha256);

            ValidatePattern(packageId, PackageIdRegex(), $"{entryContext}.packageId", errors);
            ValidatePattern(id, IdRegex(), $"{entryContext}.id", errors);
            ValidatePattern(version, VersionRegex(), $"{entryContext}.version", errors);
            ValidatePattern(maintainer, IdRegex(), $"{entryContext}.maintainer", errors);
            if (classification is not ("official" or "contributed"))
            {
                errors.Add(
                    $"{entryContext}.classification must be 'official' or 'contributed'.");
            }

            if (!string.IsNullOrEmpty(packageId) &&
                !string.IsNullOrEmpty(id) &&
                !packageId.EndsWith($".{id}", StringComparison.Ordinal))
            {
                errors.Add(
                    $"{entryContext}.packageId '{packageId}' must end with '.{id}'.");
            }

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

            SourceMetadata? source = null;
            if (entry.TryGetPropertyValue("source", out var sourceNode))
            {
                if (sourceNode is JsonObject sourceObject)
                {
                    source = ValidateSource(sourceObject, $"{entryContext}.source", errors);
                }
                else
                {
                    errors.Add($"{entryContext}.source must be an object.");
                }
            }

            if (classification == "official" && source is null)
            {
                errors.Add($"{entryContext}.source is required for an official pack.");
            }

            entries.Add(
                new CatalogEntry(
                    packageId,
                    id,
                    version,
                    classification,
                    maintainer,
                    file,
                    sha256,
                    source,
                    entryContext));
        }

        localeEntries = ValidateLocaleCatalogEntries(catalog, validateHashes, errors);
        return entries;
    }

    private static SourceCatalogMetadata ValidateSourceCatalog(
        JsonObject sourceCatalog,
        string context,
        List<string> errors)
    {
        ValidateObject(
            sourceCatalog,
            context,
            ["repository", "revision", "file", "repositoryId", "version"],
            ["repository", "revision", "file", "repositoryId", "version"],
            errors);
        ValidateExactString(
            sourceCatalog,
            "repository",
            SourceRepositoryUrl,
            context,
            errors);
        ValidateExactString(sourceCatalog, "file", SourceCatalogFile, context, errors);
        ValidateExactString(
            sourceCatalog,
            "repositoryId",
            SourceCatalogRepositoryId,
            context,
            errors);
        // The application catalog version evolves independently of the public
        // canonical catalog version; validate its format, not a frozen value.
        var sourceVersion = string.Empty;
        if (TryGetString(
                sourceCatalog, "version", context, errors, out var version))
        {
            ValidatePattern(
                version, VersionRegex(), $"{context}.version", errors);
            sourceVersion = version;
        }
        TryGetString(sourceCatalog, "revision", context, errors, out var revision);
        ValidatePattern(revision, RevisionRegex(), $"{context}.revision", errors);

        return new SourceCatalogMetadata(
            SourceRepositoryUrl,
            revision,
            SourceCatalogFile,
            SourceCatalogRepositoryId,
            sourceVersion);
    }

    private static SourceMetadata ValidateSource(
        JsonObject source,
        string context,
        List<string> errors)
    {
        ValidateObject(
            source,
            context,
            ["repository", "revision", "file", "sha256"],
            ["repository", "revision", "file", "sha256"],
            errors);
        TryGetString(source, "repository", context, errors, out var repository);
        TryGetString(source, "revision", context, errors, out var revision);
        TryGetString(source, "file", context, errors, out var file);
        TryGetString(source, "sha256", context, errors, out var sha256);
        ValidateOptionalHttpsUrl(source, "repository", context, errors);
        ValidatePattern(revision, RevisionRegex(), $"{context}.revision", errors);
        ValidatePattern(file, SourceFileRegex(), $"{context}.file", errors);
        ValidatePattern(sha256, Sha256Regex(), $"{context}.sha256", errors);
        return new SourceMetadata(repository, revision, file, sha256);
    }

    private static IReadOnlyList<LocaleCatalogEntry> ValidateLocaleCatalogEntries(
        JsonObject catalog,
        bool validateHashes,
        List<string> errors)
    {
        const string context = "catalog.json";
        if (!TryGetArray(catalog, "locales", context, errors, out var locales))
        {
            return [];
        }

        var entries = new List<LocaleCatalogEntry>();
        var localeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (node, index) in locales.Select((node, index) => (node, index)))
        {
            var entryContext = $"{context}.locales[{index}]";
            if (node is not JsonObject entry)
            {
                errors.Add($"{entryContext} must be an object.");
                continue;
            }

            var required = validateHashes
                ? new[] { "locale", "name", "file", "sha256", "source" }
                : new[] { "locale", "name", "file", "source" };
            ValidateObject(
                entry,
                entryContext,
                ["locale", "name", "file", "sha256", "source"],
                required,
                errors);
            TryGetString(entry, "locale", entryContext, errors, out var locale);
            TryGetString(entry, "name", entryContext, errors, out var name);
            TryGetString(entry, "file", entryContext, errors, out var file);
            TryGetString(entry, "sha256", entryContext, errors, out var sha256);
            ValidateLocaleTag(locale, $"{entryContext}.locale", errors);
            if (string.IsNullOrWhiteSpace(name))
            {
                errors.Add($"{entryContext}.name must be a non-empty string.");
            }

            if (!TryValidateLocaleRepositoryPath(file, out var pathError))
            {
                errors.Add($"{entryContext}.file {pathError}");
            }

            if (validateHashes)
            {
                ValidatePattern(sha256, Sha256Regex(), $"{entryContext}.sha256", errors);
            }

            ValidateUnique(localeIds, locale, $"{entryContext}.locale", errors);
            ValidateUnique(files, file, $"{entryContext}.file", errors);

            SourceMetadata? source = null;
            if (entry.TryGetPropertyValue("source", out var sourceNode))
            {
                if (sourceNode is JsonObject sourceObject)
                {
                    source = ValidateSource(sourceObject, $"{entryContext}.source", errors);
                }
                else
                {
                    errors.Add($"{entryContext}.source must be an object.");
                }
            }

            entries.Add(
                new LocaleCatalogEntry(
                    locale,
                    name,
                    file,
                    sha256,
                    source,
                    entryContext));
        }

        return entries;
    }

    private static LocaleMetadata? ValidateLocale(
        JsonObject locale,
        string relativePath,
        List<string> errors)
    {
        ValidateObject(
            locale,
            relativePath,
            [
                "$schema", "documentType", "schemaVersion", "locale", "name",
                "strings", "modifiers", "packs",
            ],
            ["$schema", "documentType", "schemaVersion", "locale", "name"],
            errors);
        ValidateExactString(locale, "$schema", LocaleSchemaPath, relativePath, errors);
        ValidateExactString(locale, "documentType", "catverter-locale", relativePath, errors);
        ValidateExactInteger(locale, "schemaVersion", 1, relativePath, errors);
        TryGetString(locale, "locale", relativePath, errors, out var localeId);
        TryGetString(locale, "name", relativePath, errors, out var name);
        ValidateLocaleTag(localeId, $"{relativePath}.locale", errors);
        if (string.IsNullOrWhiteSpace(name))
        {
            errors.Add($"{relativePath}.name must be a non-empty string.");
        }

        var fileId = Path.GetFileNameWithoutExtension(relativePath);
        if (!string.IsNullOrEmpty(localeId) &&
            !string.Equals(fileId, localeId, StringComparison.Ordinal))
        {
            errors.Add(
                $"{relativePath}.locale '{localeId}' must match the file name '{fileId}'.");
        }

        if (locale.TryGetPropertyValue("strings", out var stringsNode))
        {
            ValidateLocaleStringMap(stringsNode, $"{relativePath}.strings", errors);
        }

        var globalModifierIds = locale.TryGetPropertyValue("modifiers", out var modifiersNode)
            ? ValidateLocaleTextMap(
                modifiersNode,
                $"{relativePath}.modifiers",
                requireUnitPlaceholder: true,
                errors)
            : [];
        var packs = new Dictionary<string, LocalizedPackMetadata>(StringComparer.Ordinal);
        if (locale.TryGetPropertyValue("packs", out var packsNode))
        {
            if (packsNode is not JsonObject packObject)
            {
                errors.Add($"{relativePath}.packs must be an object.");
            }
            else
            {
                foreach (var (packId, node) in packObject)
                {
                    var context = $"{relativePath}.packs.{packId}";
                    if (!IdRegex().IsMatch(packId))
                    {
                        errors.Add($"{context} uses an invalid pack ID.");
                    }

                    if (node is not JsonObject localizedPack)
                    {
                        errors.Add($"{context} must be an object.");
                        continue;
                    }

                    packs[packId] = ValidateLocalizedPack(localizedPack, context, errors);
                }
            }
        }

        if (string.IsNullOrEmpty(localeId) || string.IsNullOrEmpty(name))
        {
            return null;
        }

        return new LocaleMetadata(localeId, name, globalModifierIds, packs);
    }

    private static void ValidateLocaleStringMap(
        JsonNode? node,
        string context,
        List<string> errors)
    {
        if (node is not JsonObject values)
        {
            errors.Add($"{context} must be an object.");
            return;
        }

        foreach (var (key, value) in values)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                errors.Add($"{context} contains an empty key.");
            }

            if (!TryGetStringValue(value, out _))
            {
                errors.Add($"{context}.{key} must be a string.");
            }
        }
    }

    private static LocalizedPackMetadata ValidateLocalizedPack(
        JsonObject localizedPack,
        string context,
        List<string> errors)
    {
        ValidateObject(
            localizedPack,
            context,
            ["name", "description", "conversions", "modifiers"],
            [],
            errors);
        var hasName = ValidateOptionalLocalizedString(localizedPack, "name", context, errors);
        var hasDescription = ValidateOptionalLocalizedString(
            localizedPack,
            "description",
            context,
            errors);
        var conversionIds = localizedPack.TryGetPropertyValue(
            "conversions",
            out var conversionsNode)
            ? ValidateLocaleTextMap(
                conversionsNode,
                $"{context}.conversions",
                requireUnitPlaceholder: false,
                errors)
            : [];
        var modifierIds = localizedPack.TryGetPropertyValue("modifiers", out var modifiersNode)
            ? ValidateLocaleTextMap(
                modifiersNode,
                $"{context}.modifiers",
                requireUnitPlaceholder: true,
                errors)
            : [];
        return new LocalizedPackMetadata(
            hasName,
            hasDescription,
            conversionIds,
            modifierIds);
    }

    private static bool ValidateOptionalLocalizedString(
        JsonObject value,
        string property,
        string context,
        List<string> errors)
    {
        if (!value.TryGetPropertyValue(property, out var node))
        {
            return false;
        }

        if (!TryGetStringValue(node, out var text) || string.IsNullOrWhiteSpace(text))
        {
            errors.Add($"{context}.{property} must be a non-empty string.");
            return false;
        }

        return true;
    }

    private static HashSet<string> ValidateLocaleTextMap(
        JsonNode? node,
        string context,
        bool requireUnitPlaceholder,
        List<string> errors)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        if (node is not JsonObject values)
        {
            errors.Add($"{context} must be an object.");
            return ids;
        }

        foreach (var (id, value) in values)
        {
            if (!IdRegex().IsMatch(id))
            {
                errors.Add($"{context}.{id} uses an invalid ID.");
            }

            ids.Add(id);
            ValidatePluralText(value, $"{context}.{id}", requireUnitPlaceholder, errors);
        }

        return ids;
    }

    private static void ValidatePluralText(
        JsonNode? node,
        string context,
        bool requireUnitPlaceholder,
        List<string> errors)
    {
        if (node is not JsonObject plural)
        {
            errors.Add($"{context} must be an object.");
            return;
        }

        ValidateObject(plural, context, ["one", "other"], ["one", "other"], errors);
        foreach (var form in new[] { "one", "other" })
        {
            if (!TryGetString(plural, form, context, errors, out var text) ||
                string.IsNullOrWhiteSpace(text))
            {
                errors.Add($"{context}.{form} must be a non-empty string.");
                continue;
            }

            if (requireUnitPlaceholder &&
                !text.Contains("{unit}", StringComparison.Ordinal))
            {
                errors.Add($"{context}.{form} must contain '{{unit}}'.");
            }
        }
    }

    private static void ValidateLocaleTag(
        string locale,
        string context,
        List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(locale))
        {
            errors.Add($"{context} must be a non-empty BCP-47 locale.");
            return;
        }

        try
        {
            _ = CultureInfo.GetCultureInfo(locale);
        }
        catch (CultureNotFoundException)
        {
            errors.Add($"{context} '{locale}' is not a recognized BCP-47 locale.");
        }
    }

    private static void ReconcileLocales(
        string root,
        IReadOnlyList<LocaleCatalogEntry> entries,
        IReadOnlyDictionary<string, LocaleMetadata> localesByPath,
        IReadOnlyDictionary<string, PackMetadata> packsByPath,
        IReadOnlyList<CatalogEntry> packEntries,
        SourceCatalogMetadata sourceCatalog,
        bool validateHashes,
        List<string> errors)
    {
        var catalogFiles = entries.Select(entry => entry.File).ToHashSet(StringComparer.Ordinal);
        foreach (var localePath in localesByPath.Keys)
        {
            if (!catalogFiles.Contains(localePath))
            {
                errors.Add($"{localePath} is not listed in catalog.json.");
            }
        }

        var packsById = packsByPath.Values
            .GroupBy(pack => pack.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var catalogPackIds = packEntries.Select(entry => entry.Id).ToHashSet(StringComparer.Ordinal);
        var officialPackIds = packEntries
            .Where(entry => entry.Classification == "official")
            .Select(entry => entry.Id)
            .ToHashSet(StringComparer.Ordinal);
        var officialModifierIds = officialPackIds
            .Where(packsById.ContainsKey)
            .SelectMany(packId => packsById[packId].ModifierIds)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var entry in entries)
        {
            if (!TryResolveLocalePath(root, entry.File, out var fullPath, out _))
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

            if (!localesByPath.TryGetValue(entry.File, out var locale))
            {
                continue;
            }

            CompareCatalogMetadata(entry.Context, "locale", entry.Locale, locale.Locale, errors);
            CompareCatalogMetadata(entry.Context, "name", entry.Name, locale.Name, errors);
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

            if (entry.Source is not null)
            {
                if (!string.Equals(
                        entry.Source.Repository,
                        sourceCatalog.Repository,
                        StringComparison.Ordinal))
                {
                    errors.Add(
                        $"{entry.Context}.source must use the catalog source repository.");
                }

                var expectedSourceFile = $"packs/{entry.File}";
                if (!string.Equals(
                        entry.Source.File,
                        expectedSourceFile,
                        StringComparison.Ordinal))
                {
                    errors.Add(
                        $"{entry.Context}.source.file must be '{expectedSourceFile}'.");
                }
            }

            foreach (var localizedPackId in locale.Packs.Keys)
            {
                if (!catalogPackIds.Contains(localizedPackId))
                {
                    errors.Add(
                        $"{entry.File}.packs contains unknown pack '{localizedPackId}'.");
                }
            }

            foreach (var globalModifierId in locale.GlobalModifierIds)
            {
                if (!officialModifierIds.Contains(globalModifierId))
                {
                    errors.Add(
                        $"{entry.File}.modifiers contains unknown modifier " +
                        $"'{globalModifierId}'.");
                }
            }

            foreach (var packId in officialPackIds)
            {
                if (!packsById.TryGetValue(packId, out var pack))
                {
                    continue;
                }

                if (!locale.Packs.TryGetValue(packId, out var localizedPack))
                {
                    errors.Add($"{entry.File}.packs is missing official pack '{packId}'.");
                    continue;
                }

                if (!localizedPack.HasName)
                {
                    errors.Add($"{entry.File}.packs.{packId}.name is required.");
                }

                if (!localizedPack.HasDescription)
                {
                    errors.Add($"{entry.File}.packs.{packId}.description is required.");
                }

                foreach (var conversionId in pack.ConversionIds)
                {
                    if (!localizedPack.ConversionIds.Contains(conversionId))
                    {
                        errors.Add(
                            $"{entry.File}.packs.{packId}.conversions is missing " +
                            $"'{conversionId}'.");
                    }
                }

                foreach (var localizedConversionId in localizedPack.ConversionIds)
                {
                    if (!pack.ConversionIds.Contains(localizedConversionId))
                    {
                        errors.Add(
                            $"{entry.File}.packs.{packId}.conversions contains unknown " +
                            $"conversion '{localizedConversionId}'.");
                    }
                }

                foreach (var modifierId in pack.ModifierIds)
                {
                    if (!localizedPack.ModifierIds.Contains(modifierId) &&
                        !locale.GlobalModifierIds.Contains(modifierId))
                    {
                        errors.Add(
                            $"{entry.File} has no localization for modifier '{modifierId}' " +
                            $"used by official pack '{packId}'.");
                    }
                }

                foreach (var localizedModifierId in localizedPack.ModifierIds)
                {
                    if (!pack.ModifierIds.Contains(localizedModifierId))
                    {
                        errors.Add(
                            $"{entry.File}.packs.{packId}.modifiers contains unknown " +
                            $"modifier '{localizedModifierId}'.");
                    }
                }
            }
        }
    }

    private static void ReconcileCatalog(
        string root,
        IReadOnlyList<CatalogEntry> entries,
        IReadOnlyDictionary<string, PackMetadata> packsByPath,
        SourceCatalogMetadata sourceCatalog,
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
            ValidateCatalogOwnership(entry, pack, sourceCatalog, errors);

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

    private static void ValidateCatalogOwnership(
        CatalogEntry entry,
        PackMetadata pack,
        SourceCatalogMetadata sourceCatalog,
        List<string> errors)
    {
        var pathMaintainer = entry.File.Split('/')[1];
        if (!string.Equals(entry.Maintainer, pathMaintainer, StringComparison.Ordinal))
        {
            errors.Add(
                $"{entry.Context}.maintainer '{entry.Maintainer}' must match the pack " +
                $"folder '{pathMaintainer}'.");
        }

        if (entry.Classification == "official")
        {
            if (!string.Equals(entry.Maintainer, OfficialMaintainer, StringComparison.Ordinal))
            {
                errors.Add(
                    $"{entry.Context}.maintainer must be '{OfficialMaintainer}' for an " +
                    "official pack.");
            }

            if (!entry.PackageId.StartsWith(OfficialPackageIdPrefix, StringComparison.Ordinal))
            {
                errors.Add(
                    $"{entry.Context}.packageId must use the reserved " +
                    $"'{OfficialPackageIdPrefix}*' namespace for an official pack.");
            }

            if (!pack.Ownership.Authors.Contains(OfficialAuthor, StringComparer.Ordinal))
            {
                errors.Add(
                    $"{entry.File}.metadata.authors must include '{OfficialAuthor}' for an " +
                    "official pack.");
            }

            if (!string.Equals(
                    pack.Ownership.License,
                    OfficialLicense,
                    StringComparison.Ordinal))
            {
                errors.Add(
                    $"{entry.File}.metadata.license must be '{OfficialLicense}' for an " +
                    "official pack.");
            }

            if (entry.Source is not null)
            {
                if (!string.Equals(
                        entry.Source.Repository,
                        sourceCatalog.Repository,
                        StringComparison.Ordinal) ||
                    !string.Equals(
                        entry.Source.Revision,
                        sourceCatalog.Revision,
                        StringComparison.Ordinal))
                {
                    errors.Add(
                        $"{entry.Context}.source must use the catalog source repository " +
                        "and revision.");
                }

                var expectedSourceFile = $"packs/{entry.Maintainer}/{entry.Id}.json";
                if (!string.Equals(
                        entry.Source.File,
                        expectedSourceFile,
                        StringComparison.Ordinal))
                {
                    errors.Add(
                        $"{entry.Context}.source.file must be '{expectedSourceFile}'.");
                }
            }
        }
        else if (entry.Classification == "contributed")
        {
            if (entry.PackageId.StartsWith(OfficialPackageIdPrefix, StringComparison.Ordinal))
            {
                errors.Add(
                    $"{entry.Context}.packageId uses the reserved official namespace " +
                    $"'{OfficialPackageIdPrefix}*'.");
            }

            if (pack.Ownership.Authors.Count == 0)
            {
                errors.Add(
                    $"{entry.File}.metadata.authors must identify at least one contributor.");
            }

            if (string.IsNullOrWhiteSpace(pack.Ownership.License))
            {
                errors.Add($"{entry.File}.metadata.license is required for a contribution.");
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

    private static bool TryResolveLocalePath(
        string root,
        string relativePath,
        out string fullPath,
        out string error)
    {
        fullPath = string.Empty;
        if (!TryValidateLocaleRepositoryPath(relativePath, out error))
        {
            return false;
        }

        var segments = relativePath.Split('/');
        fullPath = Path.GetFullPath(Path.Combine(root, segments[0], segments[1]));
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

    private static bool TryValidateLocaleRepositoryPath(string path, out string error)
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

        if (segments.Length != 2 ||
            !string.Equals(segments[0], "locales", StringComparison.Ordinal))
        {
            error = $"'{path}' must use locales/<locale>.json.";
            return false;
        }

        var fileName = segments[1];
        if (!fileName.EndsWith(".json", StringComparison.Ordinal) ||
            !IdRegex().IsMatch(fileName[..^5]))
        {
            error = $"'{path}' has an invalid locale file name.";
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
        List<string> errors,
        StringComparer? comparer = null)
    {
        if (node is not JsonArray array)
        {
            errors.Add($"{context} must be an array.");
            return [];
        }

        var values = new List<string>();
        var uniqueValues = new HashSet<string>(comparer ?? StringComparer.Ordinal);
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

    private static bool TryGetOptionalString(
        JsonObject value,
        string property,
        out string result)
    {
        result = string.Empty;
        return value.TryGetPropertyValue(property, out var node) &&
            TryGetStringValue(node, out result);
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

    [GeneratedRegex("^[0-9a-f]{40}$", RegexOptions.CultureInvariant)]
    private static partial Regex RevisionRegex();

    [GeneratedRegex(
        "^packs/(?:[a-z0-9](?:[a-z0-9-]*[a-z0-9])?/)?[a-z0-9](?:[a-z0-9-]*[a-z0-9])?\\.json$",
        RegexOptions.CultureInvariant)]
    private static partial Regex SourceFileRegex();

    [GeneratedRegex("^#[0-9A-Fa-f]{6}$", RegexOptions.CultureInvariant)]
    private static partial Regex ColorRegex();

    [GeneratedRegex("^[A-Za-z]:[/\\\\]", RegexOptions.CultureInvariant)]
    private static partial Regex WindowsDrivePathRegex();

    private sealed record PackOwnership(
        IReadOnlyList<string> Authors,
        string License,
        string Repository)
    {
        public static PackOwnership Empty { get; } = new([], string.Empty, string.Empty);
    }

    private sealed record PackMetadata(
        string PackageId,
        string Id,
        string Version,
        PackOwnership Ownership,
        IReadOnlySet<string> ModifierIds,
        IReadOnlySet<string> ConversionIds);

    private sealed record SourceCatalogMetadata(
        string Repository,
        string Revision,
        string File,
        string RepositoryId,
        string Version)
    {
        public static SourceCatalogMetadata Empty { get; } =
            new(string.Empty, string.Empty, string.Empty, string.Empty, string.Empty);
    }

    private sealed record SourceMetadata(
        string Repository,
        string Revision,
        string File,
        string Sha256);

    private sealed record CatalogEntry(
        string PackageId,
        string Id,
        string Version,
        string Classification,
        string Maintainer,
        string File,
        string Sha256,
        SourceMetadata? Source,
        string Context);

    private sealed record LocaleCatalogEntry(
        string Locale,
        string Name,
        string File,
        string Sha256,
        SourceMetadata? Source,
        string Context);

    private sealed record LocaleMetadata(
        string Locale,
        string Name,
        IReadOnlySet<string> GlobalModifierIds,
        IReadOnlyDictionary<string, LocalizedPackMetadata> Packs);

    private sealed record LocalizedPackMetadata(
        bool HasName,
        bool HasDescription,
        IReadOnlySet<string> ConversionIds,
        IReadOnlySet<string> ModifierIds);
}
