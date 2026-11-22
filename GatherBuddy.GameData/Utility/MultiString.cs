using Dalamud.Game;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using Lumina.Text.ReadOnly;

namespace GatherBuddy.Utility;

public readonly struct MultiString(string en, string de, string fr, string jp, string? chs = null)
{
    // XIVLauncherCN uses language value 4; the international SDK need not name it.
    public const ClientLanguage SimplifiedChinese = (ClientLanguage)4;

    public static string ParseSeStringLumina(ReadOnlySeString? luminaString)
        => luminaString?.ExtractText() ?? string.Empty;

    public readonly string English = en;
    public readonly string German = de;
    public readonly string French = fr;
    public readonly string Japanese = jp;
    private readonly string? _simplifiedChinese = chs;

    public string this[ClientLanguage lang]
        => Name(lang);

    public override string ToString()
        => _simplifiedChinese ?? English;

    public string ToWholeString()
        => $"{English}|{German}|{French}|{Japanese}";


    public static MultiString FromPlaceName(IDataManager gameData, uint id)
    {
        var en = ParseSeStringLumina(gameData.GetExcelSheet<PlaceName>(ClientLanguage.English).GetRowOrDefault(id)?.Name);
        var de = ParseSeStringLumina(gameData.GetExcelSheet<PlaceName>(ClientLanguage.German).GetRowOrDefault(id)?.Name);
        var fr = ParseSeStringLumina(gameData.GetExcelSheet<PlaceName>(ClientLanguage.French).GetRowOrDefault(id)?.Name);
        var jp = ParseSeStringLumina(gameData.GetExcelSheet<PlaceName>(ClientLanguage.Japanese).GetRowOrDefault(id)?.Name);
        var chs = gameData.Language == SimplifiedChinese
            ? ParseSeStringLumina(gameData.GetExcelSheet<PlaceName>(SimplifiedChinese).GetRowOrDefault(id)?.Name)
            : null;
        return new MultiString(en, de, fr, jp, string.IsNullOrEmpty(chs) ? null : chs);
    }

    public static MultiString FromItem(IDataManager gameData, uint id)
    {
        var en = ParseSeStringLumina(gameData.GetExcelSheet<Item>(ClientLanguage.English).GetRowOrDefault(id)?.Name);
        var de = ParseSeStringLumina(gameData.GetExcelSheet<Item>(ClientLanguage.German).GetRowOrDefault(id)?.Name);
        var fr = ParseSeStringLumina(gameData.GetExcelSheet<Item>(ClientLanguage.French).GetRowOrDefault(id)?.Name);
        var jp = ParseSeStringLumina(gameData.GetExcelSheet<Item>(ClientLanguage.Japanese).GetRowOrDefault(id)?.Name);
        var chs = gameData.Language == SimplifiedChinese
            ? ParseSeStringLumina(gameData.GetExcelSheet<Item>(SimplifiedChinese).GetRowOrDefault(id)?.Name)
            : null;
        return new MultiString(en, de, fr, jp, string.IsNullOrEmpty(chs) ? null : chs);
    }

    private string Name(ClientLanguage lang)
        => lang switch
        {
            ClientLanguage.English => English,
            ClientLanguage.German => German,
            ClientLanguage.Japanese => Japanese,
            ClientLanguage.French => French,
            SimplifiedChinese => _simplifiedChinese ?? English,
            _ => English,
        };

    public static readonly MultiString Empty = new(string.Empty, string.Empty, string.Empty, string.Empty);
}
