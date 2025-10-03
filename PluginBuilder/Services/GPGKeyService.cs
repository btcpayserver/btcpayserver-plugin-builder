using System.Text;
using Org.BouncyCastle.Bcpg;
using Org.BouncyCastle.Bcpg.OpenPgp;
using PluginBuilder.ViewModels;

namespace PluginBuilder.Services;

public class GPGKeyService
{
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

            var revokedKey = key.IsRevoked();
            bool revoked = key.GetSignatures().OfType<PgpSignature>().Any(sig => sig.SignatureType == PgpSignature.KeyRevocation);
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
}
