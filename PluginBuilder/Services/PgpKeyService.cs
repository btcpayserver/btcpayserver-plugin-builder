using System.Text.RegularExpressions;
using System.Text;
using Org.BouncyCastle.Bcpg.OpenPgp;
using PluginBuilder.ViewModels.Account;
using Org.BouncyCastle.Bcpg;

namespace PluginBuilder.Services;

public class PgpKeyService
{   

    public List<PgpKey> ParsePgpPublicKey(string publicKey, string title)
    {   
        string batchId = Guid.NewGuid().ToString(); // Batch ID to group master key and sub keys belonging to a public key
        List<PgpKey> pgpKeys = new List<PgpKey>();
        try
        {
            using var inputStream = new MemoryStream(Encoding.ASCII.GetBytes(publicKey));
            PgpPublicKeyRingBundle pgpPub = new PgpPublicKeyRingBundle(PgpUtilities.GetDecoderStream(inputStream));
            foreach (PgpPublicKeyRing keyRing in pgpPub.GetKeyRings())
            {
                PgpPublicKey key = keyRing.GetPublicKey();
                var userIds = keyRing.GetPublicKeys().SelectMany(key => key.GetUserIds());
                if (key != null)
                {
                    byte[] fingerprintBytes = key.GetFingerprint();
                    string fingerprint = BitConverter.ToString(fingerprintBytes).Replace("-", "");
                    string emailAddress = string.Empty;
                    foreach (string publicuserId in key.GetUserIds())
                    {
                        Match match = Regex.Match(publicuserId, @"<(.+@.+)>");
                        if (match.Success)
                        {
                            emailAddress = match.Groups[1].Value;
                            break;
                        }
                        else if (publicuserId.Contains("@"))
                        {
                            emailAddress = publicuserId;
                            break;
                        }
                    }
                    var pgpKey = new PgpKey
                    {
                        KeyBatchId = batchId,
                        Title = title,
                        PublicKey = publicKey,
                        KeyId = key.KeyId.ToString("X16"),
                        BitStrength = key.BitStrength,
                        IsEncryptionKey = key.IsEncryptionKey,
                        PublicKeyEmailAddress = emailAddress,
                        IsMasterKey = key.IsMasterKey,
                        Algorithm = key.Algorithm.ToString(),
                        AddedDate = DateTime.Now,
                        CreatedDate = key.CreationTime,
                        ValidDays = key.GetValidSeconds(),
                        Fingerprint = fingerprint,
                        Version = key.Version,
                        KeyUserId = string.Join(", ", userIds)
                    };
                    pgpKeys.Add(pgpKey);
                }
            }
        }
        catch { throw; }
        return pgpKeys;
    }
    public bool VerifyDetachedSignature(string dataToVerify, string asciiArmoredSignature, string asciiArmoredPublicKey, out string error)
    {
        byte[] dataBytes = Encoding.UTF8.GetBytes(dataToVerify);
        byte[] signatureBytes = Encoding.UTF8.GetBytes(asciiArmoredSignature);
        byte[] publicKeyBytes = Encoding.UTF8.GetBytes(asciiArmoredPublicKey);
        return VerifyDetachedSignature(dataBytes, signatureBytes, publicKeyBytes, out error);
    }

    public bool VerifyDetachedSignature(byte[] dataToVerify, byte[] signatureData, byte[] publicKeyData, out string error)
    {
        error = string.Empty;
        try
        {
            using (Stream publicKeyStream = new MemoryStream(publicKeyData))
            using (Stream signatureStream = new MemoryStream(signatureData))
            using (Stream dataStream = new MemoryStream(dataToVerify))
            {
                PgpPublicKeyRingBundle pgpPubRingCollection = new PgpPublicKeyRingBundle(
                    PgpUtilities.GetDecoderStream(publicKeyStream));
                PgpObjectFactory pgpFact = new PgpObjectFactory(PgpUtilities.GetDecoderStream(signatureStream));

                PgpSignatureList sigList = null;
                object obj = pgpFact.NextPgpObject();

                if (obj is PgpSignatureList)
                {
                    sigList = (PgpSignatureList)obj;
                }
                else if (obj is PgpOnePassSignatureList)
                {
                    obj = pgpFact.NextPgpObject();
                    sigList = (PgpSignatureList)obj;
                }

                if (sigList == null || sigList.Count == 0)
                {
                    error = "No signature found in the signature data.";
                    return false;
                }

                PgpSignature signature = sigList[0];
                PgpPublicKey publicKey = FindPublicKey(pgpPubRingCollection, signature.KeyId);
                if (publicKey == null)
                {
                    error = "Public key for signature not found in provided key data.";
                    return false;
                }

                signature.InitVerify(publicKey);
                int ch;
                while ((ch = dataStream.ReadByte()) >= 0)
                {
                    signature.Update((byte)ch);
                }

                bool result = signature.Verify();
                if (!result)
                    error = "Signature verification failed.";

                return result;
            }
        }
        catch (Exception ex)
        {
            error = $"Error verifying signature: {ex.Message}";
            return false;
        }
    }

    private PgpPublicKey FindPublicKey(PgpPublicKeyRingBundle pgpPubRingCollection, long keyId)
    {
        foreach (PgpPublicKeyRing keyRing in pgpPubRingCollection.GetKeyRings())
        {
            PgpPublicKey key = keyRing.GetPublicKey(keyId);
            if (key != null)
            {
                return key;
            }
        }
        return null;
    }
}
