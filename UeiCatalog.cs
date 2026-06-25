using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;

internal static class UeiFavorites
{
    private static readonly HashSet<string> Favorites = new(StringComparer.OrdinalIgnoreCase);

    public static void Load(string serialized)
    {
        Favorites.Clear();
        if (string.IsNullOrWhiteSpace(serialized))
        {
            return;
        }

        foreach (string raw in serialized.Split('|'))
        {
            string key = raw.Trim();
            if (!string.IsNullOrEmpty(key))
            {
                Favorites.Add(key);
            }
        }
    }

    public static bool Contains(string entryKey)
    {
        return Favorites.Contains(entryKey);
    }

    public static int Count => Favorites.Count;

    public static void Toggle(string entryKey)
    {
        if (string.IsNullOrWhiteSpace(entryKey))
        {
            return;
        }

        if (!Favorites.Remove(entryKey))
        {
            Favorites.Add(entryKey);
        }

        UeiPlugin.SaveFavorites();
    }

    public static void ClearAll()
    {
        if (Favorites.Count == 0)
        {
            return;
        }

        Favorites.Clear();
        UeiPlugin.SaveFavorites();
    }

    public static string Serialize()
    {
        return string.Join("|", Favorites.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
    }
}

internal static class RshLibHelper
{
    private static bool _available;
    private static bool _loggedNotFound;
    private static Type? _rshItemType;
    private static FieldInfo? _spriteField;
    private static System.Collections.IDictionary? _dict;

    public static Sprite? GetCustomItemSprite(string itemId)
    {
        if (!EnsureAvailable() || _dict == null || _spriteField == null)
        {
            return null;
        }

        try
        {
            string baseId = GetBaseId(itemId);
            object? rshItem = _dict[baseId];
            if (rshItem == null)
            {
                return null;
            }

            return _spriteField.GetValue(rshItem) as Sprite;
        }
        catch
        {
            return null;
        }
    }

    public static bool IsCustomItem(string itemId)
    {
        if (!EnsureAvailable() || _dict == null)
        {
            return false;
        }

        try
        {
            string baseId = GetBaseId(itemId);
            return _dict.Contains(baseId);
        }
        catch
        {
            return false;
        }
    }

    private static bool EnsureAvailable()
    {
        if (_available)
        {
            return true;
        }

        try
        {
            Type? pluginType = null;
            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                pluginType = asm.GetType("RshLib.Plugin", throwOnError: false);
                if (pluginType != null)
                {
                    break;
                }
            }

            if (pluginType == null)
            {
                if (!_loggedNotFound)
                {
                    _loggedNotFound = true;
                    UeiPlugin.LogInfo("UEI: RshLib not found, custom item support disabled.");
                }
                return false;
            }

            FieldInfo? regField = pluginType.GetField("itemRegistry",
                BindingFlags.Public | BindingFlags.Static);
            if (regField == null)
            {
                UeiPlugin.LogWarning("UEI: RshLib.Plugin found but itemRegistry field missing.");
                return false;
            }

            object? registry = regField.GetValue(null);
            if (registry == null)
            {
                UeiPlugin.LogWarning("UEI: RshLib itemRegistry is null.");
                return false;
            }

            System.Collections.IDictionary dict;
            try
            {
                dict = (System.Collections.IDictionary)registry;
            }
            catch
            {
                UeiPlugin.LogWarning("UEI: RshLib itemRegistry is not a dictionary.");
                return false;
            }

            Type? rshItemType = null;
            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                rshItemType = asm.GetType("RshLib.RshItem", throwOnError: false);
                if (rshItemType != null)
                {
                    break;
                }
            }

            if (rshItemType == null)
            {
                UeiPlugin.LogWarning("UEI: RshLib.Plugin found but RshItem type not found.");
                return false;
            }

            FieldInfo? sf = rshItemType.GetField("sprite",
                BindingFlags.Public | BindingFlags.Instance);
            if (sf == null)
            {
                UeiPlugin.LogWarning("UEI: RshItem found but sprite field missing.");
                return false;
            }

            _dict = dict;
            _rshItemType = rshItemType;
            _spriteField = sf;
            _available = true;
            UeiPlugin.LogInfo($"UEI: RshLib detected, custom item support enabled. Registry has {dict.Count} items.");
            return true;
        }
        catch (Exception ex)
        {
            UeiPlugin.LogWarning($"UEI: RshLib probe failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    public static string GetBaseId(string id)
    {
        if (string.IsNullOrEmpty(id))
        {
            return id;
        }

        int dollarIndex = id.IndexOf('$');
        return dollarIndex >= 0 ? id.Substring(0, dollarIndex) : id;
    }

    public static string TryResolveKey(string fullId, UeiEntryKind kind, Dictionary<string, UeiEntry> entriesByKey)
    {
        string entryKey = UeiCatalog.EntryKey(kind, fullId);
        if (entriesByKey.ContainsKey(entryKey))
        {
            return entryKey;
        }

        string baseId = GetBaseId(fullId);
        if (baseId != fullId)
        {
            string baseKey = UeiCatalog.EntryKey(kind, baseId);
            if (entriesByKey.ContainsKey(baseKey))
            {
                return baseKey;
            }
        }

        return entryKey;
    }
}

internal static class UeiCatalog
{
    public const string CategoryAll = "All";
    public const string CategoryFavorites = "Favorites";
    public const string CategoryLiquid = "Liquid";

    private static readonly List<UeiEntry> EntriesBacking = new();
    private static readonly List<string> CategoryKeysBacking = new();
    private static readonly Dictionary<string, UeiEntry> EntriesByKey = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, List<UeiRecipeLink>> ProducedByLinks = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, List<UeiRecipeLink>> UsedInLinks = new(StringComparer.OrdinalIgnoreCase);

    private static int _itemCount = -1;
    private static int _liquidCount = -1;
    private static int _recipeCount = -1;
    private static int _playerInt = -1;
    private static int _madeRecipeCount = -1;
    private static int _specialKnownCount = -1;
    private static int _revision;

    public static IReadOnlyList<UeiEntry> Entries => EntriesBacking;
    public static IReadOnlyList<string> CategoryKeys => CategoryKeysBacking;
    public static int Revision => _revision;

    public static void ForceRebuild()
    {
        _itemCount = -1;
        _liquidCount = -1;
        _recipeCount = -1;
        _playerInt = -1;
        _madeRecipeCount = -1;
        _specialKnownCount = -1;
        _revision++;
    }

    public static bool EnsureBuilt()
    {
        if (!IsRuntimeDataReady())
        {
            return false;
        }

        int itemCount = Item.GlobalItems.Count;
        int liquidCount = Liquids.Registry.Count;
        int recipeCount = Recipes.recipes.Count;
        int playerInt = PlayerCamera.main.body.skills.INT;
        int madeRecipeCount = Recipes.recipes.Count(r => r.hasMadeBefore);
        int specialKnownCount = Recipes.recipes.Count(r => r.specialKnown);
        if (itemCount == _itemCount
            && liquidCount == _liquidCount
            && recipeCount == _recipeCount
            && playerInt == _playerInt
            && madeRecipeCount == _madeRecipeCount
            && specialKnownCount == _specialKnownCount
            && EntriesBacking.Count > 0)
        {
            return true;
        }

        Build(itemCount, liquidCount, recipeCount, playerInt, madeRecipeCount, specialKnownCount);
        return true;
    }

    public static string EntryKey(UeiEntryKind kind, string id)
    {
        return (kind == UeiEntryKind.Item ? "item:" : "liquid:") + id;
    }

    public static bool TryGetEntry(UeiEntryKind kind, string id, out UeiEntry? entry)
    {
        string key = EntryKey(kind, id);
        if (EntriesByKey.TryGetValue(key, out entry))
        {
            return true;
        }

        string baseId = RshLibHelper.GetBaseId(id);
        if (baseId != id)
        {
            return EntriesByKey.TryGetValue(EntryKey(kind, baseId), out entry);
        }

        entry = null;
        return false;
    }

    public static List<UeiRecipeLink> GetProducedBy(UeiEntry entry)
    {
        return ProducedByLinks.TryGetValue(entry.EntryKey, out List<UeiRecipeLink>? links)
            ? links
            : new List<UeiRecipeLink>();
    }

    public static List<UeiRecipeLink> GetUsedIn(UeiEntry entry)
    {
        return UsedInLinks.TryGetValue(entry.EntryKey, out List<UeiRecipeLink>? links)
            ? links
            : new List<UeiRecipeLink>();
    }

    public static string CategoryLabel(string categoryKey)
    {
        return UeiI18n.CategoryLabel(categoryKey);
    }

    public static bool IsDangerousLiquid(string id)
    {
        return Liquids.DangerList != null && Liquids.DangerList.Contains(id);
    }

    private static bool IsRuntimeDataReady()
    {
        try
        {
            return Item.GlobalItems != null
                && Item.GlobalItems.Count > 0
                && Liquids.Registry != null
                && Liquids.Registry.Count > 0
                && Recipes.recipes != null
                && Recipes.recipes.Count > 0
                && PlayerCamera.main != null
                && PlayerCamera.main.body != null;
        }
        catch
        {
            return false;
        }
    }

    private static void Build(int itemCount, int liquidCount, int recipeCount, int playerInt, int madeRecipeCount, int specialKnownCount)
    {
        EntriesBacking.Clear();
        EntriesByKey.Clear();
        ProducedByLinks.Clear();
        UsedInLinks.Clear();

        Sprite? droplet = SafeLoadDroplet();
        foreach (KeyValuePair<string, ItemInfo> kv in Item.GlobalItems.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
        {
            UeiEntry entry = BuildItemEntry(kv.Key, kv.Value);
            EntriesBacking.Add(entry);
            EntriesByKey[entry.EntryKey] = entry;
        }

        foreach (KeyValuePair<string, LiquidType> kv in Liquids.Registry.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
        {
            UeiEntry entry = BuildLiquidEntry(kv.Key, kv.Value, droplet);
            EntriesBacking.Add(entry);
            EntriesByKey[entry.EntryKey] = entry;
        }

        BuildCategories();
        BuildRecipeLinks();

        _itemCount = itemCount;
        _liquidCount = liquidCount;
        _recipeCount = recipeCount;
        _playerInt = playerInt;
        _madeRecipeCount = madeRecipeCount;
        _specialKnownCount = specialKnownCount;
        _revision++;
        UeiPlugin.LogInfo($"UEI catalog built: {EntriesBacking.Count} entries, {Recipes.recipes.Count} recipes.");
    }

    private static UeiEntry BuildItemEntry(string id, ItemInfo info)
    {
        string name = UeiI18n.ItemText(id, string.Empty);
        if (string.IsNullOrWhiteSpace(name))
        {
            name = SafeString(() => info.fullName, id);
        }

        string desc = UeiI18n.ItemText(id + "dsc", string.Empty);
        if (string.IsNullOrWhiteSpace(desc))
        {
            desc = SafeString(() => info.description, string.Empty);
        }

        string category = string.IsNullOrWhiteSpace(info.category) ? "other" : info.category.Trim();
        UeiEntry entry = new()
        {
            Kind = UeiEntryKind.Item,
            Id = id,
            EntryKey = EntryKey(UeiEntryKind.Item, id),
            DisplayName = name,
            Description = desc,
            CategoryKey = category,
            CategoryLabel = CategoryLabel(category),
            Icon = SafeLoadItemSprite(id),
            IconColor = Color.white,
            ItemInfo = info,
        };

        AddTags(entry, info);
        AddQualities(entry, info.qualities);
        entry.SearchText = BuildSearchText(entry);
        return entry;
    }

    private static UeiEntry BuildLiquidEntry(string id, LiquidType info, Sprite? droplet)
    {
        string name = UeiI18n.OtherText(id, id);
        if (string.IsNullOrWhiteSpace(name) || name == id)
        {
            string localeName = SafeString(() => info.localeName, id);
            name = UeiI18n.OtherText(localeName, localeName);
        }

        UeiEntry entry = new()
        {
            Kind = UeiEntryKind.Liquid,
            Id = id,
            EntryKey = EntryKey(UeiEntryKind.Liquid, id),
            DisplayName = name,
            Description = UeiI18n.OtherText(id + "dsc", string.Empty),
            CategoryKey = CategoryLiquid,
            CategoryLabel = CategoryLabel(CategoryLiquid),
            Icon = droplet,
            IconColor = info.color,
            LiquidInfo = info,
        };

        if (info.injectable)
        {
            entry.Tags.Add("injectable");
        }
        if (info.healthUsable)
        {
            entry.Tags.Add("health");
        }
        if (IsDangerousLiquid(id))
        {
            entry.Tags.Add("danger");
        }
        AddQualities(entry, info.qualities);
        entry.SearchText = BuildSearchText(entry);
        return entry;
    }

    private static void BuildCategories()
    {
        CategoryKeysBacking.Clear();
        CategoryKeysBacking.Add(CategoryAll);
        CategoryKeysBacking.Add(CategoryFavorites);

        foreach (string category in EntriesBacking
            .Where(x => x.Kind == UeiEntryKind.Item)
            .Select(x => x.CategoryKey)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            CategoryKeysBacking.Add(category);
        }

        CategoryKeysBacking.Add(CategoryLiquid);
    }

    private static void BuildRecipeLinks()
    {
        for (int i = 0; i < Recipes.recipes.Count; i++)
        {
            Recipe recipe = Recipes.recipes[i];
            AddProducedLink(recipe, i);
            AddUsedLinks(recipe, i);
        }
    }

    private static void AddProducedLink(Recipe recipe, int recipeIndex)
    {
        if (recipe.result == null || string.IsNullOrWhiteSpace(recipe.result.id))
        {
            return;
        }

        string fullId = recipe.result.id;
        UeiEntryKind kind = recipe.result.isLiquid ? UeiEntryKind.Liquid : UeiEntryKind.Item;
        string key = RshLibHelper.TryResolveKey(fullId, kind, EntriesByKey);
        if (!EntriesByKey.ContainsKey(key) && !EntriesByKey.ContainsKey(EntryKey(kind, fullId)))
        {
            return;
        }

        if (!EntriesByKey.ContainsKey(key))
        {
            key = EntryKey(kind, fullId);
        }

        GetOrCreate(ProducedByLinks, key).Add(NewRecipeLink(recipe, recipeIndex, UeiRecipeRelation.ProducedBy));
    }

    private static void AddUsedLinks(Recipe recipe, int recipeIndex)
    {
        if (recipe.items == null)
        {
            return;
        }

        HashSet<string> added = new(StringComparer.OrdinalIgnoreCase);
        foreach (RecipeItem item in recipe.items)
        {
            foreach (UeiEntry entry in EntriesBacking)
            {
                if (!RecipeItemUsesEntry(item, entry) || !added.Add(entry.EntryKey))
                {
                    continue;
                }

                GetOrCreate(UsedInLinks, entry.EntryKey).Add(NewRecipeLink(recipe, recipeIndex, UeiRecipeRelation.UsedIn));
            }
        }
    }

    private static bool RecipeItemUsesEntry(RecipeItem item, UeiEntry entry)
    {
        bool wantsLiquid = item.isLiquid;
        if (wantsLiquid != (entry.Kind == UeiEntryKind.Liquid))
        {
            return false;
        }

        bool specific = item.specific || !string.IsNullOrWhiteSpace(item.specificId);
        if (specific)
        {
            string specificId = item.specificId ?? string.Empty;
            if (string.Equals(specificId, entry.Id, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            string specificBase = RshLibHelper.GetBaseId(specificId);
            return string.Equals(specificBase, entry.Id, StringComparison.OrdinalIgnoreCase);
        }

        if (item.quality == null || string.IsNullOrWhiteSpace(item.quality.id))
        {
            return false;
        }

        if (entry.Kind == UeiEntryKind.Liquid)
        {
            return entry.Qualities.Any(q => string.Equals(q.id, item.quality.id, StringComparison.OrdinalIgnoreCase)
                && q.amount > 0f);
        }

        return entry.Qualities.Any(q => string.Equals(q.id, item.quality.id, StringComparison.OrdinalIgnoreCase)
            && q.amount >= item.quality.amount);
    }

    private static UeiRecipeLink NewRecipeLink(Recipe recipe, int recipeIndex, UeiRecipeRelation relation)
    {
        return new UeiRecipeLink
        {
            RecipeIndex = recipeIndex,
            Relation = relation,
            Visible = SafeBool(() => recipe.visible, true),
            HasMadeBefore = recipe.hasMadeBefore,
        };
    }

    private static List<UeiRecipeLink> GetOrCreate(Dictionary<string, List<UeiRecipeLink>> map, string key)
    {
        if (!map.TryGetValue(key, out List<UeiRecipeLink>? links))
        {
            links = new List<UeiRecipeLink>();
            map[key] = links;
        }

        return links;
    }

    private static void AddTags(UeiEntry entry, ItemInfo info)
    {
        try
        {
            string[]? tags = info.GetTags();
            if (tags != null)
            {
                foreach (string tag in tags)
                {
                    if (!string.IsNullOrWhiteSpace(tag))
                    {
                        entry.Tags.Add(tag.Trim());
                    }
                }
            }
        }
        catch
        {
            if (!string.IsNullOrWhiteSpace(info.tags))
            {
                foreach (string tag in info.tags.Split(','))
                {
                    if (!string.IsNullOrWhiteSpace(tag))
                    {
                        entry.Tags.Add(tag.Trim());
                    }
                }
            }
        }
    }

    private static void AddQualities(UeiEntry entry, List<CraftingQuality>? qualities)
    {
        if (qualities == null)
        {
            return;
        }

        foreach (CraftingQuality quality in qualities)
        {
            if (quality != null && !string.IsNullOrWhiteSpace(quality.id))
            {
                entry.Qualities.Add(quality);
            }
        }
    }

    private static string BuildSearchText(UeiEntry entry)
    {
        StringBuilder sb = new();
        sb.Append(entry.Id).Append(' ');
        sb.Append(entry.EntryKey).Append(' ');
        sb.Append(entry.DisplayName).Append(' ');
        sb.Append(entry.Description).Append(' ');
        sb.Append(entry.CategoryKey).Append(' ');
        sb.Append(entry.CategoryLabel).Append(' ');
        foreach (string tag in entry.Tags)
        {
            sb.Append(tag).Append(' ');
        }
        foreach (CraftingQuality quality in entry.Qualities)
        {
            sb.Append(quality.id).Append(' ');
            sb.Append(SafeString(() => quality.LocaleName, string.Empty)).Append(' ');
        }
        return sb.ToString();
    }

    private static Sprite? SafeLoadItemSprite(string id)
    {
        Sprite? customSprite = RshLibHelper.GetCustomItemSprite(id);
        if (customSprite != null)
        {
            return customSprite;
        }

        try
        {
            GameObject prefab = Resources.Load<GameObject>(id);
            if (prefab == null)
            {
                return null;
            }

            SpriteRenderer renderer = prefab.GetComponent<SpriteRenderer>();
            return renderer != null ? renderer.sprite : null;
        }
        catch
        {
            return null;
        }
    }

    private static Sprite? SafeLoadDroplet()
    {
        try { return Resources.Load<Sprite>("Sprites/droplet"); }
        catch { return null; }
    }

    private static string SafeString(Func<string> getter, string fallback)
    {
        try
        {
            string value = getter();
            return string.IsNullOrWhiteSpace(value) ? fallback : value;
        }
        catch
        {
            return fallback;
        }
    }

    private static bool SafeBool(Func<bool> getter, bool fallback)
    {
        try { return getter(); }
        catch { return fallback; }
    }
}
