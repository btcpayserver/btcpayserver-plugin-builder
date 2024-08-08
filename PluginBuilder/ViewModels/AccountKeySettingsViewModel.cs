namespace PluginBuilder.ViewModels
{
    public class AccountKeySettingsViewModel
    {
        public string PrivateKey { get; set; }
        public string PublicKey { get; set; }
    }

    public class PluginApprovalStatusUpdateViewModel
    {
        public string PluginSlug { get; set; }
        public string PublicKey { get; set; }
        public string RejectionReason { get; set; }
    }
}
