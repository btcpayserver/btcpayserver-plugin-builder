using Microsoft.AspNetCore.Identity;
using Org.BouncyCastle.Asn1.Cmp;
using Org.BouncyCastle.Bcpg.OpenPgp;
using PluginBuilder.ViewModels;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace PluginBuilder.Services;

public class PgpKeyService
{
    private DBConnectionFactory ConnectionFactory { get; }
    private UserManager<IdentityUser> UserManager { get; }
    public PgpKeyService(DBConnectionFactory connectionFactory,
            UserManager<IdentityUser> userManager)
    {
        UserManager = userManager;
        ConnectionFactory = connectionFactory;
    }

    public async Task AddNewPGGKeyAsync(string publicKey, string title, string userId)
    {
        await using var conn = await ConnectionFactory.Open();
        var accountSettings = await conn.GetAccountDetailSettings(userId) ?? new AccountSettings();

        // Batch ID to group master key and sub keys belonging to a public key
        string batchId = Guid.NewGuid().ToString();
        List<PgpKey> pgpKeys = new List<PgpKey>();
        using var keyStream = new MemoryStream(Encoding.ASCII.GetBytes(publicKey));
        using var inputStream = PgpUtilities.GetDecoderStream(keyStream);
        var pgpPubKeyBundle = new PgpPublicKeyRingBundle(inputStream);

        var emailRegex = new Regex(@"[a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,}");
        bool emailMatches = false;
        var userRecord = await UserManager.FindByIdAsync(userId);
        foreach (PgpPublicKeyRing keyRing in pgpPubKeyBundle.GetKeyRings())
        {
            foreach (PgpPublicKey key in keyRing.GetPublicKeys())
            {
                var userIds = key.GetUserIds();
                string extractedEmails = string.Empty;
                if (userIds.Any(userIdString =>
                {
                    var match = emailRegex.Match(userIdString);
                    if (match.Success)
                    {
                        extractedEmails = match.Value; 
                        return match.Value.Equals(userRecord?.Email, StringComparison.OrdinalIgnoreCase);
                    }
                    return false;
                }))
                {
                    emailMatches = true;
                }
                if (!emailMatches)
                {
                    continue;
                }
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
                    KeyUserId = string.Join(", ", userIds)
                };
                pgpKeys.Add(pgpKey);
            }
        }
        accountSettings.PgpKeys = pgpKeys;
        await conn.SetAccountDetailSettings(accountSettings, userId);
    }

    public string ComputeSHA256(string input)
    {
        using SHA256 sha256 = SHA256.Create();
        byte[] inputBytes = Encoding.UTF8.GetBytes(input);
        byte[] hashBytes = sha256.ComputeHash(inputBytes);
        return BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
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

    public (bool success, string response) VerifyPgpMessage(string armoredMessage, string manifestShasum, List<string> publicKeys)
    {
        try
        {
            foreach (var publicKey in publicKeys)
            {
                PgpPublicKey pgpPublicKey = LoadPublicKey(publicKey);
                if (pgpPublicKey == null)
                {
                    continue;
                }
                using var messageStream = new MemoryStream(Encoding.UTF8.GetBytes(armoredMessage));
                using var decoderStream = PgpUtilities.GetDecoderStream(messageStream);
                var pgpFactory = new PgpObjectFactory(PgpUtilities.GetDecoderStream(messageStream));
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
                        if (!message.Contains(manifestShasum))
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
            return (false, "Unable to validate signature message with account public keys");
        }
        catch (Exception)
        {

            throw;
        }
    }
}
