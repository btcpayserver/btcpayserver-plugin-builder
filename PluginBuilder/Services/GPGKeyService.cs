using System.Text;
using Newtonsoft.Json;
using Org.BouncyCastle.Bcpg;
using Org.BouncyCastle.Bcpg.OpenPgp;
using PluginBuilder.DataModels;
using PluginBuilder.Util;
using PluginBuilder.Util.Extensions;
using PluginBuilder.ViewModels;

namespace PluginBuilder.Services;

public class GPGKeyService
{
    private readonly DBConnectionFactory _connectionFactory;

    public GPGKeyService(DBConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public bool ValidateArmouredPublicKey(string publicKey, out string message, out PgpKeyViewModel? vm)
    {
        vm = null;
        if (publicKey.Contains("-----BEGIN PGP PRIVATE KEY BLOCK-----", StringComparison.OrdinalIgnoreCase))
        {
            message = "Private key block detected; upload only the public key";
            return false;
        }
        try
        {
            using var publicKeyStream = new MemoryStream(Encoding.ASCII.GetBytes(publicKey));
            using var decoderStream = PgpUtilities.GetDecoderStream(publicKeyStream);
            var keyRingBundle = new PgpPublicKeyRingBundle(decoderStream);
            PgpPublicKey? key = null;

            foreach (PgpPublicKeyRing keyRing in keyRingBundle.GetKeyRings())
            {
                foreach (PgpPublicKey k in keyRing.GetPublicKeys())
                {
                    if (!k.IsEncryptionKey || k.IsMasterKey)
                    {
                        key = k;
                        break;
                    }
                }
                if (key != null)
                    break;
            }

            if (key == null)
            {
                message = "Signing key is required";
                return false;
            }

            bool canSign = key.Algorithm switch
            {
                PublicKeyAlgorithmTag.RsaGeneral or
                PublicKeyAlgorithmTag.RsaSign or
                PublicKeyAlgorithmTag.Dsa or
                PublicKeyAlgorithmTag.ECDsa or
                PublicKeyAlgorithmTag.EdDsa => true,
                _ => false
            };
            if (!canSign)
            {
                message = "Public key provided does not support signing";
                return false;
            }

            if (key.GetValidSeconds() != 0 && key.CreationTime.AddSeconds(key.GetValidSeconds()) <= DateTimeOffset.UtcNow)
            {
                message = "Key has expired";
                return false;
            }

            if (key.IsRevoked() || key.GetSignatures().OfType<PgpSignature>().Any(sig => sig.SignatureType == PgpSignature.KeyRevocation))
            {
                message = "Key is revoked";
                return false;
            }
            vm = new PgpKeyViewModel
            {
                KeyId = key.KeyId.ToString("X"),
                Fingerprint = BitConverter.ToString(key.GetFingerprint()).Replace("-", ""),
                PublicKey = publicKey,
                CreatedDate = key.CreationTime,
                AddedDate = DateTimeOffset.UtcNow,
                ValidDays = key.GetValidSeconds() == 0 ? -1 : (long)(key.CreationTime.AddSeconds(key.GetValidSeconds()) - key.CreationTime).TotalDays,
                Version = key.Version
            };
            message = "Public Key validated successfully";
            return true;
        }
        catch
        {
            message = "An error occured while validating public key";
            return false;
        }
    }


    public bool VerifyDetachedSignature(string pluginslug, string userId, string armouredSignature, byte[] rawSignedBytes, out string message)
    {
        message = null;
        try
        {
            var publicKey = GetPluginOwnerPublicKeys(pluginslug, userId).GetAwaiter().GetResult();
            if (string.IsNullOrEmpty(publicKey))
            {
                message = "No public keys found for this user. Kindly update your account profile with your GPG public key";
                return false;
            }
            using Stream pubIn = new MemoryStream(Encoding.ASCII.GetBytes(publicKey));
            PgpPublicKeyRingBundle pubBundle = new PgpPublicKeyRingBundle(PgpUtilities.GetDecoderStream(pubIn));

            using Stream sigIn = new MemoryStream(Encoding.ASCII.GetBytes(armouredSignature));
            PgpObjectFactory sigFact = new PgpObjectFactory(PgpUtilities.GetDecoderStream(sigIn));
            PgpSignatureList sigList = (PgpSignatureList)sigFact.NextPgpObject();
            if (sigList.Count == 0)
            {
                message = "No signature found in armour.";
                return false;
            }

            PgpSignature signature = sigList[0];
            PgpPublicKey signingKey = pubBundle.GetPublicKey(signature.KeyId);
            if (signingKey == null)
            {
                message = "Signature was made with a key not not associated with the user's public key";
                return false;
            }

            signature.InitVerify(signingKey);
            signature.Update(rawSignedBytes);
            bool ok = signature.Verify();
            message = ok ? "Signature valid." : "Unable to verify signature. Commit mismatch";
            return ok;
        }
        catch (Exception ex)
        {
            message = $"Verification failed: {ex.Message}";
            return false;
        }
    }


    private async Task<string> GetPluginOwnerPublicKeys(string pluginSlug, string userId)
    {
        await using var conn = await _connectionFactory.Open();
        var pluginOwners = await conn.GetPluginOwners(pluginSlug);
        if (pluginOwners?.Any() == false) return string.Empty;

        var owner = pluginOwners.FirstOrDefault(o => o.UserId == userId);
        if (owner == null || string.IsNullOrEmpty(owner.AccountDetail)) return string.Empty;

        var accountSettings = JsonConvert.DeserializeObject<AccountSettings>(owner.AccountDetail, CamelCaseSerializerSettings.Instance);
        if (accountSettings?.GPGKey == null || string.IsNullOrEmpty(accountSettings.GPGKey.PublicKey)) return string.Empty;

        return accountSettings.GPGKey.PublicKey;
    }
}
