#nullable disable
using Microsoft.AspNetCore.Mvc.ViewFeatures;

namespace PluginBuilder.Util.Extensions;

public static class ViewDataExtensions
{
    private const string ACTIVE_CATEGORY_KEY = "ActiveCategory";
    private const string ACTIVE_PAGE_KEY = "ActivePage";
    private const string ACTIVE_ID_KEY = "ActiveId";
    private const string ActivePageClass = "active";

    public static void SetActivePage<T>(this ViewDataDictionary viewData, T activePage, string title = null, string activeId = null)
        where T : IConvertible
    {
        viewData.SetActivePage(activePage.ToString(), activePage.GetType().ToString(), title, activeId);
    }

    public static void SetActivePage(this ViewDataDictionary viewData, string activePage, string category, string title = null, string activeId = null)
    {
        // Page Title
        viewData["Title"] = title ?? activePage;
        // Navigation
        viewData[ACTIVE_PAGE_KEY] = activePage;
        viewData[ACTIVE_ID_KEY] = activeId;
        viewData.SetActiveCategory(category);
    }

    public static void SetActiveCategory<T>(this ViewDataDictionary viewData, T activeCategory)
    {
        viewData.SetActiveCategory(activeCategory.ToString());
    }

    public static void SetActiveCategory(this ViewDataDictionary viewData, string activeCategory)
    {
        viewData[ACTIVE_CATEGORY_KEY] = activeCategory;
    }

    public static string IsActivePage<T>(this ViewDataDictionary viewData, T page, object id = null)
        where T : IConvertible
    {
        return viewData.IsActivePage(page.ToString(), page.GetType().ToString(), id);
    }

    public static string IsActivePage<T>(this ViewDataDictionary viewData, IEnumerable<T> pages, object id = null)
        where T : IConvertible
    {
        return pages.Any(page => viewData.IsActivePage(page.ToString(), page.GetType().ToString(), id) == ActivePageClass)
            ? ActivePageClass
            : null;
    }

    public static string IsActivePage(this ViewDataDictionary viewData, string page, string category, object id = null)
    {
        if (!viewData.ContainsKey(ACTIVE_PAGE_KEY))
            return null;
        var activeId = viewData[ACTIVE_ID_KEY];
        var activePage = viewData[ACTIVE_PAGE_KEY]?.ToString();
        var activeCategory = viewData[ACTIVE_CATEGORY_KEY]?.ToString();
        var categoryAndPageMatch = (category == null || activeCategory.Equals(category, StringComparison.InvariantCultureIgnoreCase)) &&
                                   page.Equals(activePage, StringComparison.InvariantCultureIgnoreCase);
        var idMatch = id == null || activeId == null || id.Equals(activeId);
        return categoryAndPageMatch && idMatch ? ActivePageClass : null;
    }
}
