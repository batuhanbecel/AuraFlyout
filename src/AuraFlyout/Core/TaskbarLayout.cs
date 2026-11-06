namespace AuraFlyout.Core;

/// <summary>
/// The order of the taskbar items as one left-to-right list with two zone separators:
/// [left items…] "|" [centre items…] "|" [right items…, next to the tray].
/// Moving an item past a separator moves it to the neighbouring zone.
/// </summary>
public static class TaskbarLayout
{
    public const string Separator = "|";
    public static readonly string[] Ids = ["media", "status", "speed", "logi", "mic"];

    /// <summary>The stored list, repaired (both separators, every item exactly once).</summary>
    public static List<string> Get()
    {
        var s = SettingsStore.Current;
        var list = s.TaskbarLayout.Count > 0 ? new List<string>(s.TaskbarLayout) : FromLegacy(s);
        return Normalize(list);
    }

    public static void Set(List<string> list) => SettingsStore.Current.TaskbarLayout = Normalize(list);

    /// <summary>Zone (0 left, 1 centre, 2 right) and position inside the zone, left to right.</summary>
    public static (int Zone, int Index) Locate(List<string> list, string id)
    {
        int zone = 0, index = 0;
        foreach (string item in list)
        {
            if (item == Separator)
            {
                zone++;
                index = 0;
                continue;
            }
            if (item == id) return (zone, index);
            index++;
        }
        return (0, int.MaxValue);
    }

    /// <summary>Moves an item one step left (-1) or right (+1); may cross into the next zone.</summary>
    public static List<string> Move(List<string> list, string id, int direction)
    {
        var l = new List<string>(list);
        int i = l.IndexOf(id);
        int j = i + direction;
        if (i < 0 || j < 0 || j >= l.Count) return l;
        (l[i], l[j]) = (l[j], l[i]);
        return l;
    }

    private static List<string> Normalize(List<string> list)
    {
        var result = new List<string>();
        int separators = 0;
        foreach (string item in list)
        {
            if (item == Separator)
            {
                if (separators < 2) result.Add(Separator);
                separators++;
            }
            else if (Ids.Contains(item) && !result.Contains(item))
            {
                result.Add(item);
            }
        }
        while (separators++ < 2) result.Add(Separator);
        foreach (string id in Ids)
            if (!result.Contains(id))
            {
                if (id == "mic") result.Add(id); // the microphone indicator belongs next to the tray, like Windows' own
                else result.Insert(0, id);       // other forgotten items start on the left
            }
        return result;
    }

    /// <summary>Builds the list from the older per-item anchor + order settings.</summary>
    private static List<string> FromLegacy(AppSettings s)
    {
        var items = new List<(string Id, int Anchor, int Order)>
        {
            ("media", Math.Clamp(s.TaskbarWidgetPosition, 0, 2), s.TaskbarMediaOrder),
            ("status", s.StatusAnchor, s.StatusOrder),
            ("speed", s.SpeedAnchor, s.SpeedOrder),
            ("logi", s.LogiAnchor, s.LogiOrder),
        };
        var list = new List<string>();
        list.AddRange(items.Where(x => x.Anchor == 0).OrderBy(x => x.Order).Select(x => x.Id));
        list.Add(Separator);
        list.AddRange(items.Where(x => x.Anchor == 1).OrderBy(x => x.Order).Select(x => x.Id));
        list.Add(Separator);
        // on the right, a lower order used to mean "closer to the tray", i.e. further right
        list.AddRange(items.Where(x => x.Anchor == 2).OrderByDescending(x => x.Order).Select(x => x.Id));
        return list;
    }
}
