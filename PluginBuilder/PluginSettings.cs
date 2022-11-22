#nullable disable
using System.ComponentModel.DataAnnotations;
using Newtonsoft.Json;

namespace PluginBuilder
{
    public class PluginSettings
    {
        [MaxLength(200)]
        [Display(Name = "Documentation link")]
        public string Documentation { get; set; }
    }
}
