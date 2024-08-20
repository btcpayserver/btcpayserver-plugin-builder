using System.Text;
using Org.BouncyCastle.Bcpg.OpenPgp;
using PluginBuilder.ViewModels;

namespace PluginBuilder.Services;

public class PgpKeyService
{
    private DBConnectionFactory ConnectionFactory { get; }
    public PgpKeyService(
        DBConnectionFactory connectionFactory)
    {
        ConnectionFactory = connectionFactory;
    }

    public async Task AddNewPGGKeyAsync(string publicKey, string title, string userId)
    {
        await using var conn = await ConnectionFactory.Open();
        var accountSettings = await conn.GetAccountDetailSettings(userId) ?? new AccountSettings();

        // Batch ID to group master key and sub keys belonging to a public key
        string batchId = Guid.NewGuid().ToString();
        List<PgpKey> pgpKeys = new List<PgpKey>();
        try
        {
            using var keyStream = new MemoryStream(Encoding.ASCII.GetBytes(publicKey));
            using var inputStream = PgpUtilities.GetDecoderStream(keyStream);
            var pgpPubKeyBundle = new PgpPublicKeyRingBundle(inputStream);
            foreach (PgpPublicKeyRing keyRing in pgpPubKeyBundle.GetKeyRings())
            {
                foreach (PgpPublicKey key in keyRing.GetPublicKeys())
                {
                    var pgpKey = new PgpKey
                    {
                        KeyBatchId = batchId,
                        Title = title,
                        PublicKey = publicKey,
                        KeyId = key.KeyId.ToString("X"),
                        BitStrength = key.BitStrength,
                        IsEncryptionKey = key.IsEncryptionKey,
                        IsMasterKey = key.IsMasterKey,
                        Algorithm = key.Algorithm.ToString(),
                        AddedDate = DateTime.Now,
                        CreatedDate = key.CreationTime,
                        ValidDays = key.GetValidSeconds(),
                        Version = key.Version,
                        KeyUserId = string.Join(", ", key.GetUserIds())
                    };
                    pgpKeys.Add(pgpKey);
                }
            }
            accountSettings.PgpKeys = pgpKeys;
            await conn.SetAccountDetailSettings(accountSettings, userId);
        }
        catch (Exception)
        {
            throw;
        }
    }

    private static PgpPublicKey LoadPublicKey(string publicKeyString)
    {
        try
        {
            using var keyStream = new MemoryStream(Encoding.ASCII.GetBytes(publicKeyString));
            using var inputStream = PgpUtilities.GetDecoderStream(keyStream);
            var pgpPubKeyBundle = new PgpPublicKeyRingBundle(inputStream);
            return pgpPubKeyBundle.GetKeyRings()
                .Cast<PgpPublicKeyRing>()
                .SelectMany(keyRing => keyRing.GetPublicKeys().Cast<PgpPublicKey>())
                .FirstOrDefault(key => key.IsMasterKey);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public (bool success, string response) VerifyPgpMessage(PluginApprovalStatusUpdateViewModel model, List<string> publicKeys)
    {
        foreach (var publicKey in publicKeys)
        {
            PgpPublicKey pgpPublicKey = LoadPublicKey(publicKey);
            if (pgpPublicKey == null)
            {
                continue;
            }

            try
            {
                using var messageStream = new MemoryStream(Encoding.UTF8.GetBytes(model.ArmoredMessage));
                using var decoderStream = PgpUtilities.GetDecoderStream(messageStream);
                var pgpFactory = new PgpObjectFactory(decoderStream);
                var pgpObject = pgpFactory.NextPgpObject();

                if (pgpObject is PgpCompressedData compressedData)
                {
                    pgpFactory = new PgpObjectFactory(compressedData.GetDataStream());
                    pgpObject = pgpFactory.NextPgpObject();
                }

                if (pgpObject is PgpOnePassSignatureList onePassSignatureList)
                {
                    var onePassSignature = onePassSignatureList[0];
                    onePassSignature.InitVerify(pgpPublicKey);

                    pgpObject = pgpFactory.NextPgpObject();
                    if (pgpObject is PgpLiteralData literalData)
                    {
                        using Stream literalDataStream = literalData.GetInputStream();
                        using var actualMessageStream = new MemoryStream();
                        int ch;
                        while ((ch = literalDataStream.ReadByte()) >= 0)
                        {
                            onePassSignature.Update((byte)ch);
                            actualMessageStream.WriteByte((byte)ch);
                        }
                        var message = Encoding.UTF8.GetString(actualMessageStream.ToArray());
                        if (!message.Contains(model.PluginSlug))
                        {
                            continue;
                        }
                    }
                    var signatureList = (PgpSignatureList)pgpFactory.NextPgpObject();
                    var signature = signatureList[0];

                    if (onePassSignature.Verify(signature))
                    {
                        return (true, "Signature verified successfully");
                    }
                }
            }
            catch (Exception)
            {
                continue;
            }
        }

        return (false, "Unable to validate signature message with account public keys");
    }

}
