#nullable disable
using Microsoft.AspNetCore.Mvc.ViewFeatures;

namespace PluginBuilder
{
    public static class ViewDataExtensions
    {
        private const string ACTIVE_CATEGORY_KEY = "ActiveCategory";
        private const string ACTIVE_PAGE_KEY = "ActivePage";
        private const string ACTIVE_ID_KEY = "ActiveId";
        private const string ACTIVE_CLASS = "active";

        public static void SetActivePage<T>(this ViewDataDictionary viewData, T activePage, string title = null, string activeId = null)
    where T : IConvertible
        {
            SetActivePage(viewData, activePage.ToString(), activePage.GetType().ToString(), title, activeId);
        }

        public static void SetActivePage(this ViewDataDictionary viewData, string activePage, string category, string title = null, string activeId = null)
        {
            // Page Title
            viewData["Title"] = title ?? activePage;
            // Navigation
            viewData[ACTIVE_PAGE_KEY] = activePage;
            viewData[ACTIVE_ID_KEY] = activeId;
            SetActiveCategory(viewData, category);
        }
        public static void SetActiveCategory<T>(this ViewDataDictionary viewData, T activeCategory)
        {
            SetActiveCategory(viewData, activeCategory.ToString());
        }

        public static void SetActiveCategory(this ViewDataDictionary viewData, string activeCategory)
        {
            viewData[ACTIVE_CATEGORY_KEY] = activeCategory;
        }

        public static string ActivePageClass<T>(this ViewDataDictionary viewData, T page, object id = null)
            where T : IConvertible
        {
            return ActivePageClass(viewData, page.ToString(), page.GetType().ToString(), id);
        }

        public static string ActivePageClass(this ViewDataDictionary viewData, string page, string category, object id = null)
        {
            return IsActivePage(viewData, page, category, id);
        }

        public static string ActivePageClass<T>(this ViewDataDictionary viewData, IEnumerable<T> pages, object id = null) where T : IConvertible
        {
            return IsActivePage(viewData, pages, id);
        }

        public static string IsActivePage<T>(this ViewDataDictionary viewData, T page, object id = null)
          where T : IConvertible
        {
            return IsActivePage(viewData, page.ToString(), page.GetType().ToString(), id);
        }
        public static bool IsActiveCategory(this ViewDataDictionary viewData, string category, object id = null)
        {
            if (!viewData.ContainsKey(ACTIVE_CATEGORY_KEY))
                return false;
            var activeId = viewData[ACTIVE_ID_KEY];
            var activeCategory = viewData[ACTIVE_CATEGORY_KEY]?.ToString();
            var categoryMatch = category.Equals(activeCategory, StringComparison.InvariantCultureIgnoreCase);
            var idMatch = id == null || activeId == null || id.Equals(activeId);
            return categoryMatch && idMatch;
        }

        public static bool IsActiveCategory<T>(this ViewDataDictionary viewData, T category, object id = null)
        {
            return IsActiveCategory(viewData, category.ToString(), id);
        }

        public static string IsActivePage<T>(this ViewDataDictionary viewData, IEnumerable<T> pages, object id = null)
            where T : IConvertible
        {
            return pages.Any(page => IsActivePage(viewData, page.ToString(), page.GetType().ToString(), id) == ACTIVE_CLASS)
                ? ACTIVE_CLASS
                : null;
        }

        public static string IsActivePage(this ViewDataDictionary viewData, string page, string category, object id = null)
        {
            if (!viewData.ContainsKey(ACTIVE_PAGE_KEY))
            {
                return null;
            }
            var activeId = viewData[ACTIVE_ID_KEY];
            var activePage = viewData[ACTIVE_PAGE_KEY]?.ToString();
            var activeCategory = viewData[ACTIVE_CATEGORY_KEY]?.ToString();
            var categoryAndPageMatch = (category == null || activeCategory.Equals(category, StringComparison.InvariantCultureIgnoreCase)) && page.Equals(activePage, StringComparison.InvariantCultureIgnoreCase);
            var idMatch = id == null || activeId == null || id.Equals(activeId);
            return categoryAndPageMatch && idMatch ? ACTIVE_CLASS : null;
        }
    }
}
