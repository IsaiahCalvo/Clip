using System.Text.Json;
using Clip.Core;

namespace Clip.Tests;

// The source-generated JSON contexts are Clip.Core's persistence and IPC contract: history.json,
// the summary/key indexes, asset sidecars, open-with-recent.json and the watcher's list output
// all flow through them. Runtime code serializes exclusively via the generated fast path, so
// these tests also push every root type through the metadata path (a fresh JsonSerializerOptions
// over the same resolver) and assert full round-trip fidelity in both directions.
public sealed class JsonContextRoundTripTests
{
    [Fact]
    public void HistoryItemRoundTripsThroughFastAndMetadataPaths()
    {
        var item = FullyPopulatedItem();

        var fastJson = JsonSerializer.Serialize(item, ClipboardHistoryJsonContext.Default.ClipboardHistoryItem);
        AssertItemEquals(item, JsonSerializer.Deserialize(fastJson, ClipboardHistoryJsonContext.Default.ClipboardHistoryItem)!);

        var options = new JsonSerializerOptions { TypeInfoResolver = ClipboardHistoryJsonContext.Default };
        var metadataJson = JsonSerializer.Serialize(item, typeof(ClipboardHistoryItem), options);
        AssertItemEquals(item, JsonSerializer.Deserialize<ClipboardHistoryItem>(metadataJson, options)!);

        var listJson = JsonSerializer.Serialize(new List<ClipboardHistoryItem> { item }, typeof(List<ClipboardHistoryItem>), options);
        AssertItemEquals(item, Assert.Single(JsonSerializer.Deserialize<List<ClipboardHistoryItem>>(listJson, options)!));
    }

    [Fact]
    public void AssetMetadataRoundTripsThroughFastAndMetadataPaths()
    {
        var metadata = new ClipboardAssetMetadata { Id = "id1", Kind = "Text", ContentHash = "AB12" };

        var fastJson = JsonSerializer.Serialize(metadata, ClipboardHistoryJsonContext.Default.ClipboardAssetMetadata);
        var restored = JsonSerializer.Deserialize(fastJson, ClipboardHistoryJsonContext.Default.ClipboardAssetMetadata)!;
        Assert.Equal("id1", restored.Id);
        Assert.Equal("Text", restored.Kind);
        Assert.Equal("AB12", restored.ContentHash);

        var options = new JsonSerializerOptions { TypeInfoResolver = ClipboardHistoryJsonContext.Default };
        var metadataJson = JsonSerializer.Serialize(metadata, typeof(ClipboardAssetMetadata), options);
        Assert.Equal("AB12", JsonSerializer.Deserialize<ClipboardAssetMetadata>(metadataJson, options)!.ContentHash);
    }

    [Fact]
    public void SummaryContextRoundTripsFullAndMinimalItems()
    {
        // The minimal item exercises every WhenWritingNull skip branch; the full one writes them all.
        var items = new List<ClipboardHistoryItem>
        {
            FullyPopulatedItem(),
            new() { Kind = ClipboardItemKind.Text, Preview = "minimal" },
        };

        var fastJson = JsonSerializer.Serialize(items, ClipboardHistorySummaryJsonContext.Default.ListClipboardHistoryItem);
        var restored = JsonSerializer.Deserialize(fastJson, ClipboardHistorySummaryJsonContext.Default.ListClipboardHistoryItem)!;
        Assert.Equal(2, restored.Count);
        AssertItemEquals(items[0], restored[0]);
        Assert.Null(restored[1].Text);

        var options = new JsonSerializerOptions { TypeInfoResolver = ClipboardHistorySummaryJsonContext.Default };
        var metadataJson = JsonSerializer.Serialize(items, typeof(List<ClipboardHistoryItem>), options);
        AssertItemEquals(items[0], JsonSerializer.Deserialize<List<ClipboardHistoryItem>>(metadataJson, options)![0]);
    }

    [Fact]
    public void KeyContextRoundTripsKeyItems()
    {
        var key = ClipboardHistoryKeyItem.From(FullyPopulatedItem());

        var fastJson = JsonSerializer.Serialize(key, ClipboardHistoryKeyJsonContext.Default.ClipboardHistoryKeyItem);
        var restored = JsonSerializer.Deserialize(fastJson, ClipboardHistoryKeyJsonContext.Default.ClipboardHistoryKeyItem)!;
        Assert.Equal(key.Id, restored.Id);
        Assert.Equal(key.Kind, restored.Kind);
        Assert.Equal(key.ContentHash, restored.ContentHash);
        Assert.Equal(key.IsPinned, restored.IsPinned);
        Assert.Equal(key.LastUsedAt, restored.LastUsedAt);

        var options = new JsonSerializerOptions { TypeInfoResolver = ClipboardHistoryKeyJsonContext.Default };
        var listJson = JsonSerializer.Serialize(new List<ClipboardHistoryKeyItem> { key }, typeof(List<ClipboardHistoryKeyItem>), options);
        var restoredList = JsonSerializer.Deserialize<List<ClipboardHistoryKeyItem>>(listJson, options)!;
        Assert.Equal(key.LastUsedAt, Assert.Single(restoredList).LastUsedAt);
    }

    [Fact]
    public void ListResultRoundTripsThroughFastAndMetadataPaths()
    {
        var action = new ClipboardHistoryListAction("open", "Open", "Clip.Watcher.exe", ["open", "id1"], RequiresFullItem: true);
        var full = new ClipboardHistoryListItem(
            Id: "id1",
            Kind: "Image",
            Title: "shot",
            Preview: "shot",
            FilePaths: ["C:\\pics\\a.png"],
            IsPinned: true,
            PinOrder: 3,
            HasOriginalFormatting: true,
            SourceApplication: "Paint",
            AssetSizeBytes: 2048,
            CharacterCount: 12,
            WordCount: 3,
            LastUsedAt: DateTimeOffset.UtcNow,
            LastCopiedAt: DateTimeOffset.UtcNow,
            CopyCount: 2,
            ImageWidth: 640,
            ImageHeight: 480,
            DefaultActionId: "open",
            Actions: [action])
        {
            AssetPath = "C:\\assets\\a.png",
        };
        var minimal = new ClipboardHistoryListItem(
            Id: "id2",
            Kind: "Text",
            Title: "t",
            Preview: "t",
            FilePaths: [],
            IsPinned: false,
            PinOrder: 0,
            HasOriginalFormatting: false,
            SourceApplication: null,
            AssetSizeBytes: null,
            CharacterCount: null,
            WordCount: null,
            LastUsedAt: DateTimeOffset.UtcNow,
            LastCopiedAt: DateTimeOffset.UtcNow,
            CopyCount: 1,
            ImageWidth: null,
            ImageHeight: null,
            DefaultActionId: null,
            Actions: []);
        var result = new ClipboardHistoryListResult(1, "history", "query", 10, 2, 5, [full, minimal]);

        var fastJson = JsonSerializer.Serialize(result, ClipboardHistoryListJsonContext.Default.ClipboardHistoryListResult);
        var restored = JsonSerializer.Deserialize(fastJson, ClipboardHistoryListJsonContext.Default.ClipboardHistoryListResult)!;
        Assert.Equal(2, restored.Items.Count);
        Assert.Equal("open", Assert.Single(restored.Items[0].Actions).Id);
        Assert.Equal(2048, restored.Items[0].AssetSizeBytes);
        Assert.Null(restored.Items[1].DefaultActionId);

        var options = new JsonSerializerOptions { TypeInfoResolver = ClipboardHistoryListJsonContext.Default };
        var metadataJson = JsonSerializer.Serialize(result, typeof(ClipboardHistoryListResult), options);
        var restored2 = JsonSerializer.Deserialize<ClipboardHistoryListResult>(metadataJson, options)!;
        Assert.Equal(result.Source, restored2.Source);
        Assert.Equal(640, restored2.Items[0].ImageWidth);
        Assert.Equal(["open", "id1"], restored2.Items[0].Actions[0].Arguments);

        var actionJson = JsonSerializer.Serialize(action, typeof(ClipboardHistoryListAction), options);
        Assert.Equal("Open", JsonSerializer.Deserialize<ClipboardHistoryListAction>(actionJson, options)!.Label);
    }

    [Fact]
    public void OpenWithContextRoundTripsRecentAppsAndStartApps()
    {
        var data = new Dictionary<string, List<OpenWithRecentStore.RecentApp>>
        {
            [".txt"] =
            [
                new OpenWithRecentStore.RecentApp("Editor", "C:\\ed.exe", null),
                new OpenWithRecentStore.RecentApp("Store", null, "Pkg!App"),
            ],
        };

        var fastJson = JsonSerializer.Serialize(data, OpenWithJsonContext.Default.DictionaryStringListRecentApp);
        var restored = JsonSerializer.Deserialize(fastJson, OpenWithJsonContext.Default.DictionaryStringListRecentApp)!;
        Assert.Equal("Editor", restored[".txt"][0].Name);
        Assert.Equal("Pkg!App", restored[".txt"][1].AppKey);

        var options = new JsonSerializerOptions { TypeInfoResolver = OpenWithJsonContext.Default };
        var metadataJson = JsonSerializer.Serialize(data, typeof(Dictionary<string, List<OpenWithRecentStore.RecentApp>>), options);
        var restored2 = JsonSerializer.Deserialize<Dictionary<string, List<OpenWithRecentStore.RecentApp>>>(metadataJson, options)!;
        Assert.Equal("C:\\ed.exe", restored2[".txt"][0].ExecutablePath);

        var startApps = new List<PackagedAppDiscovery.StartAppJson>
        {
            new() { Name = "App", AppID = "A!B" },
            new(),
        };
        var startFast = JsonSerializer.Serialize(startApps, OpenWithJsonContext.Default.ListStartAppJson);
        var restoredApps = JsonSerializer.Deserialize(startFast, OpenWithJsonContext.Default.ListStartAppJson)!;
        Assert.Equal("A!B", restoredApps[0].AppID);
        Assert.Null(restoredApps[1].Name);

        var startMetadata = JsonSerializer.Serialize(startApps, typeof(List<PackagedAppDiscovery.StartAppJson>), options);
        Assert.Equal(2, JsonSerializer.Deserialize<List<PackagedAppDiscovery.StartAppJson>>(startMetadata, options)!.Count);
    }

    [Fact]
    public void ContextsRespectRuntimeRegisteredConverters()
    {
        // Registering a runtime converter disables the generated fast path entirely, so every
        // property getter runs through the metadata graph and enums serialize as strings.
        var item = FullyPopulatedItem();

        var historyOptions = ConverterOptions(ClipboardHistoryJsonContext.Default);
        var historyJson = JsonSerializer.Serialize(item, typeof(ClipboardHistoryItem), historyOptions);
        Assert.Contains("\"Image\"", historyJson);
        AssertItemEquals(item, JsonSerializer.Deserialize<ClipboardHistoryItem>(historyJson, historyOptions)!);
        var metadataJson = JsonSerializer.Serialize(
            new ClipboardAssetMetadata { Id = "i", Kind = "Text", ContentHash = "C" }, typeof(ClipboardAssetMetadata), historyOptions);
        Assert.Equal("C", JsonSerializer.Deserialize<ClipboardAssetMetadata>(metadataJson, historyOptions)!.ContentHash);
        var listJson = JsonSerializer.Serialize(new List<ClipboardHistoryItem> { item }, typeof(List<ClipboardHistoryItem>), historyOptions);
        Assert.Single(JsonSerializer.Deserialize<List<ClipboardHistoryItem>>(listJson, historyOptions)!);

        var summaryOptions = ConverterOptions(ClipboardHistorySummaryJsonContext.Default);
        var summaryJson = JsonSerializer.Serialize(new List<ClipboardHistoryItem> { item }, typeof(List<ClipboardHistoryItem>), summaryOptions);
        AssertItemEquals(item, Assert.Single(JsonSerializer.Deserialize<List<ClipboardHistoryItem>>(summaryJson, summaryOptions)!));

        var keyOptions = ConverterOptions(ClipboardHistoryKeyJsonContext.Default);
        var key = ClipboardHistoryKeyItem.From(item);
        var keyJson = JsonSerializer.Serialize(new List<ClipboardHistoryKeyItem> { key }, typeof(List<ClipboardHistoryKeyItem>), keyOptions);
        Assert.Contains("\"Image\"", keyJson);
        Assert.Equal(key.Id, Assert.Single(JsonSerializer.Deserialize<List<ClipboardHistoryKeyItem>>(keyJson, keyOptions)!).Id);

        var listCtxOptions = ConverterOptions(ClipboardHistoryListJsonContext.Default);
        var action = new ClipboardHistoryListAction("open", "Open", "Clip.Watcher.exe", ["open", "x"], RequiresFullItem: false);
        var result = new ClipboardHistoryListResult(1, "history", null, 5, 0, 1, []);
        var resultJson = JsonSerializer.Serialize(result, typeof(ClipboardHistoryListResult), listCtxOptions);
        Assert.Equal("history", JsonSerializer.Deserialize<ClipboardHistoryListResult>(resultJson, listCtxOptions)!.Source);
        var actionJson = JsonSerializer.Serialize(action, typeof(ClipboardHistoryListAction), listCtxOptions);
        Assert.Equal("open", JsonSerializer.Deserialize<ClipboardHistoryListAction>(actionJson, listCtxOptions)!.Id);

        var openWithOptions = ConverterOptions(OpenWithJsonContext.Default);
        var recents = new Dictionary<string, List<OpenWithRecentStore.RecentApp>>
        {
            [".md"] = [new OpenWithRecentStore.RecentApp("Pad", "C:\\pad.exe", "Pad!App")],
        };
        var recentsJson = JsonSerializer.Serialize(recents, typeof(Dictionary<string, List<OpenWithRecentStore.RecentApp>>), openWithOptions);
        Assert.Equal("Pad", JsonSerializer.Deserialize<Dictionary<string, List<OpenWithRecentStore.RecentApp>>>(recentsJson, openWithOptions)![".md"][0].Name);
        var startJson = JsonSerializer.Serialize(
            new List<PackagedAppDiscovery.StartAppJson> { new() { Name = "N", AppID = "A" } },
            typeof(List<PackagedAppDiscovery.StartAppJson>), openWithOptions);
        Assert.Equal("A", Assert.Single(JsonSerializer.Deserialize<List<PackagedAppDiscovery.StartAppJson>>(startJson, openWithOptions)!).AppID);
    }

    private static JsonSerializerOptions ConverterOptions(System.Text.Json.Serialization.JsonSerializerContext context)
    {
        var options = new JsonSerializerOptions { TypeInfoResolver = context };
        options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
        return options;
    }

    [Fact]
    public void ContextsResolveTypeInfoForAuxiliaryTypes()
    {
        var history = new ClipboardHistoryJsonContext(new JsonSerializerOptions());
        Assert.NotNull(history.GetTypeInfo(typeof(List<string>)));
        Assert.NotNull(history.GetTypeInfo(typeof(string)));
        Assert.NotNull(history.GetTypeInfo(typeof(bool)));
        Assert.NotNull(history.GetTypeInfo(typeof(int)));
        Assert.NotNull(history.GetTypeInfo(typeof(long)));
        Assert.NotNull(history.GetTypeInfo(typeof(DateTimeOffset)));

        var summary = new ClipboardHistorySummaryJsonContext(new JsonSerializerOptions());
        Assert.NotNull(summary.GetTypeInfo(typeof(List<string>)));
        Assert.NotNull(summary.GetTypeInfo(typeof(string)));
        Assert.NotNull(summary.GetTypeInfo(typeof(bool)));
        Assert.NotNull(summary.GetTypeInfo(typeof(int)));
        Assert.NotNull(summary.GetTypeInfo(typeof(long)));
        Assert.NotNull(summary.GetTypeInfo(typeof(DateTimeOffset)));
        Assert.Null(summary.GetTypeInfo(typeof(Uri)));

        var key = new ClipboardHistoryKeyJsonContext(new JsonSerializerOptions());
        Assert.NotNull(key.GetTypeInfo(typeof(string)));
        Assert.NotNull(key.GetTypeInfo(typeof(bool)));
        Assert.NotNull(key.GetTypeInfo(typeof(DateTimeOffset)));
        Assert.Null(key.GetTypeInfo(typeof(Uri)));

        var list = new ClipboardHistoryListJsonContext(new JsonSerializerOptions());
        Assert.NotNull(list.GetTypeInfo(typeof(IReadOnlyList<string>)));
        Assert.NotNull(list.GetTypeInfo(typeof(IReadOnlyList<ClipboardHistoryListItem>)));
        Assert.NotNull(list.GetTypeInfo(typeof(IReadOnlyList<ClipboardHistoryListAction>)));
        Assert.NotNull(list.GetTypeInfo(typeof(string)));
        Assert.NotNull(list.GetTypeInfo(typeof(bool)));
        Assert.NotNull(list.GetTypeInfo(typeof(int)));
        Assert.NotNull(list.GetTypeInfo(typeof(long)));
        Assert.NotNull(list.GetTypeInfo(typeof(long?)));
        Assert.NotNull(list.GetTypeInfo(typeof(DateTimeOffset)));
        Assert.Null(list.GetTypeInfo(typeof(Uri)));

        var openWith = new OpenWithJsonContext(new JsonSerializerOptions());
        Assert.NotNull(openWith.GetTypeInfo(typeof(List<OpenWithRecentStore.RecentApp>)));
        Assert.NotNull(openWith.GetTypeInfo(typeof(string)));
    }

    [Fact]
    public void ContextsResolveTypeInfoForTheirRootTypesOnly()
    {
        Assert.NotNull(new ClipboardHistoryJsonContext(new JsonSerializerOptions()).GetTypeInfo(typeof(ClipboardHistoryItem)));
        Assert.NotNull(new ClipboardHistorySummaryJsonContext(new JsonSerializerOptions()).GetTypeInfo(typeof(List<ClipboardHistoryItem>)));
        Assert.NotNull(new ClipboardHistoryKeyJsonContext(new JsonSerializerOptions()).GetTypeInfo(typeof(ClipboardHistoryKeyItem)));
        Assert.NotNull(new ClipboardHistoryListJsonContext(new JsonSerializerOptions()).GetTypeInfo(typeof(ClipboardHistoryListResult)));
        Assert.NotNull(new OpenWithJsonContext(new JsonSerializerOptions()).GetTypeInfo(typeof(List<PackagedAppDiscovery.StartAppJson>)));

        Assert.Null(new ClipboardHistoryJsonContext(new JsonSerializerOptions()).GetTypeInfo(typeof(Uri)));
        Assert.Null(new OpenWithJsonContext(new JsonSerializerOptions()).GetTypeInfo(typeof(Uri)));
    }

    private static ClipboardHistoryItem FullyPopulatedItem() => new()
    {
        Id = "item-1",
        Kind = ClipboardItemKind.Image,
        Preview = "preview",
        CustomTitle = "title",
        ContentHash = "HASH",
        Text = "text",
        HtmlText = "<b>h</b>",
        RtfText = "{\\rtf1}",
        HasOriginalFormatting = true,
        AssetPath = "C:\\assets\\a.png",
        FilePaths = ["C:\\files\\a.txt", "C:\\files\\b.txt"],
        IsPinned = true,
        PinOrder = 2,
        CreatedAt = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero),
        LastUsedAt = new DateTimeOffset(2024, 1, 2, 0, 0, 0, TimeSpan.Zero),
        FirstCopiedAt = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero),
        LastCopiedAt = new DateTimeOffset(2024, 1, 3, 0, 0, 0, TimeSpan.Zero),
        CopyCount = 4,
        SourceApplication = "Paint",
        SourceApplicationPath = "C:\\apps\\paint.exe",
        SourceAppUserModelId = "Paint!App",
        AssetSizeBytes = 1024,
        ImageWidth = 640,
        ImageHeight = 480,
        OcrText = "recognized",
        CharacterCount = 4,
        WordCount = 1,
    };

    private static void AssertItemEquals(ClipboardHistoryItem expected, ClipboardHistoryItem actual)
    {
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.Kind, actual.Kind);
        Assert.Equal(expected.Preview, actual.Preview);
        Assert.Equal(expected.CustomTitle, actual.CustomTitle);
        Assert.Equal(expected.ContentHash, actual.ContentHash);
        Assert.Equal(expected.Text, actual.Text);
        Assert.Equal(expected.HtmlText, actual.HtmlText);
        Assert.Equal(expected.RtfText, actual.RtfText);
        Assert.Equal(expected.HasOriginalFormatting, actual.HasOriginalFormatting);
        Assert.Equal(expected.AssetPath, actual.AssetPath);
        Assert.Equal(expected.FilePaths, actual.FilePaths);
        Assert.Equal(expected.IsPinned, actual.IsPinned);
        Assert.Equal(expected.PinOrder, actual.PinOrder);
        Assert.Equal(expected.CreatedAt, actual.CreatedAt);
        Assert.Equal(expected.LastUsedAt, actual.LastUsedAt);
        Assert.Equal(expected.FirstCopiedAt, actual.FirstCopiedAt);
        Assert.Equal(expected.LastCopiedAt, actual.LastCopiedAt);
        Assert.Equal(expected.CopyCount, actual.CopyCount);
        Assert.Equal(expected.SourceApplication, actual.SourceApplication);
        Assert.Equal(expected.SourceApplicationPath, actual.SourceApplicationPath);
        Assert.Equal(expected.SourceAppUserModelId, actual.SourceAppUserModelId);
        Assert.Equal(expected.AssetSizeBytes, actual.AssetSizeBytes);
        Assert.Equal(expected.ImageWidth, actual.ImageWidth);
        Assert.Equal(expected.ImageHeight, actual.ImageHeight);
        Assert.Equal(expected.OcrText, actual.OcrText);
        Assert.Equal(expected.CharacterCount, actual.CharacterCount);
        Assert.Equal(expected.WordCount, actual.WordCount);
    }
}
