using System.Text;
using Newtonsoft.Json;
using Org.BouncyCastle.Bcpg;
using Org.BouncyCastle.Bcpg.OpenPgp;
using PluginBuilder.DataModels;
using PluginBuilder.Util;
using PluginBuilder.Util.Extensions;
using PluginBuilder.ViewModels;

namespace PluginBuilder.Services;

public class GPGKeyService(DBConnectionFactory connectionFactory)
{
    public bool ValidateArmouredPublicKey(string publicKey, out string message, out PgpKeyViewModel? vm)
    {
        publicKey = publicKey.Trim();
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
                    if (k.GetSignatures().All(sig => ((sig.GetHashedSubPackets()?.GetKeyFlags()) & PgpKeyFlags.CanSign) == 0)) continue;
                    key = k;
                    break;
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

            if (key.IsRevoked() || key.GetSignatures().Any(sig => sig.SignatureType == PgpSignature.KeyRevocation))
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
            message = "An error occurred while validating public key";
            return false;
        }
    }

    public async Task<SignatureProofResponse> VerifyDetachedSignature(string pluginslug, string userId, byte[] rawSignedBytes, IFormFile? signatureFile)
    {
        try
        {
            if (signatureFile is not { Length: > 0 })
                return new SignatureProofResponse(false, "Please upload a valid GPG signature file (.asc)");

            string signatureText;
            using (var reader = new StreamReader(signatureFile.OpenReadStream()))
                signatureText = await reader.ReadToEndAsync();

            var publicKey = await GetPluginOwnerPublicKeys(pluginslug, userId);
            if (string.IsNullOrEmpty(publicKey))
            {
                return new SignatureProofResponse(false, "No public keys found for this user. Kindly update your account profile with your GPG public key");
            }
            await using Stream pubIn = new MemoryStream(Encoding.ASCII.GetBytes(publicKey));
            PgpPublicKeyRingBundle pubBundle = new PgpPublicKeyRingBundle(PgpUtilities.GetDecoderStream(pubIn));

            await using Stream sigIn = new MemoryStream(Encoding.ASCII.GetBytes(signatureText));
            PgpObjectFactory sigFact = new PgpObjectFactory(PgpUtilities.GetDecoderStream(sigIn));
            PgpSignatureList sigList = (PgpSignatureList)sigFact.NextPgpObject();

            if (sigList.Count <= 0)
            {
                return new SignatureProofResponse(false, "No signature found in armoured file uploaded");
            }

            PgpSignature signature = sigList[0];
            PgpPublicKey signingKey = pubBundle.GetPublicKey(signature.KeyId);
            if (signingKey == null)
            {
                return new SignatureProofResponse(false, "File was signed with a key not associated with the user's public key");
            }
            signature.InitVerify(signingKey);
            signature.Update(rawSignedBytes);
            bool ok = signature.Verify();
            if (!ok)
            {
                return new SignatureProofResponse(false, "Unable to verify signature. Commit mismatch");
            }
            var signatureProof = new SignatureProof
            {
                Armour = signatureText,
                KeyId = signature.KeyId.ToString("X"),
                Fingerprint = BitConverter.ToString(signingKey.GetFingerprint()).Replace("-", ""),
                SignedAt = signature.CreationTime,
                VerifiedAt = DateTimeOffset.UtcNow
            };
            return new SignatureProofResponse(true, "Signature verified successfully", signatureProof);
        }
        catch (Exception ex)
        {
            return new SignatureProofResponse(false, $"Verification failed: {ex.Message}");
        }
    }

    private async Task<string> GetPluginOwnerPublicKeys(string pluginSlug, string userId)
    {
        await using var conn = await connectionFactory.Open();
        var pluginOwners = await conn.GetPluginOwners(pluginSlug);
        if (pluginOwners == null || pluginOwners.Count != 0 == false) return string.Empty;

        var owner = pluginOwners.FirstOrDefault(o => o.UserId == userId);
        if (owner == null || string.IsNullOrEmpty(owner.AccountDetail)) return string.Empty;

        var accountSettings = JsonConvert.DeserializeObject<AccountSettings>(owner.AccountDetail, CamelCaseSerializerSettings.Instance);
        if (accountSettings?.GPGKey == null || string.IsNullOrEmpty(accountSettings.GPGKey.PublicKey)) return string.Empty;

        return accountSettings.GPGKey.PublicKey;
    }
}
