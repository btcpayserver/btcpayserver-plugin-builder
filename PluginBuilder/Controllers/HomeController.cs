using Microsoft.AspNetCore.Mvc;
using PluginBuilder.Services;

namespace PluginBuilder.Controllers
{
    public class HomeController : Controller
    {
        public DBConnectionFactory ConnectionFactory { get; }
        public HomeController(DBConnectionFactory connectionFactory)
        {
            ConnectionFactory = connectionFactory;
        }
        [HttpGet("/")]
        public IActionResult HomePage()
        {
            return View();
        }

        [HttpPost("/plugins/add")]
        public IActionResult AddPlugin(
            string name,
            string repository,
            string reference,
            string csprojPath)
        {

            // Wouter style: https://github.com/storefront-bvba/btcpayserver-kraken-plugin
            // Dennis style: https://github.com/dennisreimann/btcpayserver
            // Kukks sytle: https://github.com/Kukks/btcpayserver/tree/plugins/collection/Plugins
            return View();
        }
    }
}
