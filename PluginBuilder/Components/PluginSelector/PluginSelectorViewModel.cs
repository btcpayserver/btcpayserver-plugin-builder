#nullable disable
using System.Collections.Generic;

namespace PluginBuilder.Components.PluginSelector
{
    public class PluginSelectorViewModel
    {
        public List<PluginSelectorOption> Options { get; set; }
        public PluginSlug PluginSlug { get; set; }
    }

    public class PluginSelectorOption
    {
        public bool Selected { get; set; }
        public string Text { get; set; }
        public string Value { get; set; }
        public PluginSlug PluginSlug { get; set; }
    }
}
