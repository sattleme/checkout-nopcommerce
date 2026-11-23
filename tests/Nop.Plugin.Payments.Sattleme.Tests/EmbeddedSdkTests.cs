using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Nop.Plugin.Payments.Sattleme.Services;
using Nop.Plugin.Payments.Sattleme.Tests.Support;
using NopSattleme.Sattleme.Checkout;
using NopSattleme.Sattleme.Checkout.Internal;
using NopSattleme.Sattleme.Checkout.Internal.Crypto;
using NUnit.Framework;

namespace Nop.Plugin.Payments.Sattleme.Tests;

/// <summary>
/// The SDK as the plugin ships it (its sources compiled under the plugin's names, NBitcoin.Secp256k1
/// with them), against the SDK's own conformance vectors that touch the cryptography: integration
/// keys, BIP-340 signatures, NIP-44 encryption. And the real client, made by the plugin, without the
/// network.
/// </summary>
[TestFixture]
public sealed class EmbeddedSdkTests
{
    private static byte[] Hex(string hex)
    {
        return Convert.FromHexString(hex);
    }

    private static JsonElement Vectors(params string[] path)
    {
        return JsonDocument.Parse(File.ReadAllText(Repository.File(["sdk", .. path]))).RootElement;
    }

    [Test]
    public void Reads_the_integration_keys_of_the_specification()
    {
        var cases = Vectors("spec", "vectors", "integration-keys.json").GetProperty("cases").EnumerateArray().ToList();
        Assert.That(cases, Has.Count.GreaterThan(10));
        foreach (var vector in cases)
        {
            var name = vector.GetProperty("name").GetString();
            var key = vector.GetProperty("key").ValueKind == JsonValueKind.String ? vector.GetProperty("key").GetString() : null;
            var expected = vector.GetProperty("expected");
            if (expected.TryGetProperty("error", out var error))
            {
                var thrown = Assert.Catch<SattlemeException>(() => IntegrationKey.Parse(key), name ?? "");
                Assert.That(thrown!.Code, Is.EqualTo(error.GetString()), name);
                continue;
            }
            var parsed = IntegrationKey.Parse(key);
            Assert.That(parsed.TeamPublicKey, Is.EqualTo(expected.GetProperty("teamPublicKey").GetString()), name);
            Assert.That(Convert.ToHexStringLower(parsed.Key), Is.EqualTo(expected.GetProperty("integrationKey").GetString()), name);
            Assert.That(parsed.Relays, Is.EqualTo(expected.GetProperty("relays").EnumerateArray().Select(relay => relay.GetString()).ToArray()), name);
        }
    }

    [Test]
    public void Signs_and_verifies_as_BIP_340_says()
    {
        var rows = File.ReadAllLines(Repository.File("sdk", "tests", "Sattleme.Checkout.Tests", "Vectors", "bip340", "test-vectors.csv"))
            .Skip(1).Where(line => line.Length > 0).Select(line => line.Split(',')).ToList();
        Assert.That(rows, Has.Count.EqualTo(19));
        // An event's id, the only message the SDK signs or verifies, is 32 bytes: the vectors of other lengths are left out, as the SDK's own tests do.
        var cases = rows.Where(row => row[4].Length == 64).ToList();
        Assert.That(cases, Has.Count.EqualTo(15));
        foreach (var row in cases)
        {
            var (secret, publicKey, aux, message, signature, valid) = (row[1].ToLowerInvariant(), row[2].ToLowerInvariant(), row[3].ToLowerInvariant(), row[4].ToLowerInvariant(), row[5].ToLowerInvariant(), row[6] == "TRUE");
            if (secret.Length > 0)
            {
                Assert.That(Secp256k1.PublicKeyOf(Hex(secret)), Is.EqualTo(publicKey), row[0]);
                Assert.That(Convert.ToHexStringLower(Secp256k1.Sign(Hex(message), Hex(secret), Hex(aux))), Is.EqualTo(signature), row[0]);
            }
            Assert.That(Secp256k1.Verify(signature, message, publicKey), Is.EqualTo(valid), row[0]);
        }
    }

    [Test]
    public void Encrypts_and_decrypts_as_NIP_44_says()
    {
        // The app's copy of the official vectors (github.com/paulmillr/nip44), as the SDK's own tests read it.
        var vectors = Vectors("spec", "vectors", "nip44", "nip44.vectors.json");
        var count = 0;
        foreach (var vector in vectors.GetProperty("get_conversation_key").EnumerateArray())
        {
            Assert.That(Convert.ToHexStringLower(Nip44.ConversationKey(Hex(vector.GetProperty("sec1").GetString()!), vector.GetProperty("pub2").GetString()!)),
                Is.EqualTo(vector.GetProperty("conversation_key").GetString()));
            count++;
        }
        foreach (var vector in vectors.GetProperty("get_message_keys").GetProperty("keys").EnumerateArray())
        {
            var (chachaKey, chachaNonce, hmacKey) = Nip44.MessageKeys(Hex(vectors.GetProperty("get_message_keys").GetProperty("conversation_key").GetString()!), Hex(vector.GetProperty("nonce").GetString()!));
            Assert.That(Convert.ToHexStringLower(chachaKey), Is.EqualTo(vector.GetProperty("chacha_key").GetString()));
            Assert.That(Convert.ToHexStringLower(chachaNonce), Is.EqualTo(vector.GetProperty("chacha_nonce").GetString()));
            Assert.That(Convert.ToHexStringLower(hmacKey), Is.EqualTo(vector.GetProperty("hmac_key").GetString()));
            count++;
        }
        foreach (var pair in vectors.GetProperty("calc_padded_len").EnumerateArray())
        {
            Assert.That(Nip44.PaddedLength(pair[0].GetInt32()), Is.EqualTo(pair[1].GetInt32()));
            count++;
        }
        foreach (var vector in vectors.GetProperty("encrypt_decrypt").EnumerateArray())
        {
            var key = Hex(vector.GetProperty("conversation_key").GetString()!);
            Assert.That(Nip44.EncryptWithNonce(vector.GetProperty("plaintext").GetString()!, key, Hex(vector.GetProperty("nonce").GetString()!)), Is.EqualTo(vector.GetProperty("payload").GetString()));
            Assert.That(Nip44.Decrypt(vector.GetProperty("payload").GetString()!, key), Is.EqualTo(vector.GetProperty("plaintext").GetString()));
            count++;
        }
        foreach (var vector in vectors.GetProperty("invalid_get_conversation_key").EnumerateArray())
        {
            Assert.Catch<ArgumentException>(() => Nip44.ConversationKey(Hex(vector.GetProperty("sec1").GetString()!), vector.GetProperty("pub2").GetString()!), vector.GetProperty("note").GetString() ?? "");
            count++;
        }
        foreach (var vector in vectors.GetProperty("invalid_decrypt").EnumerateArray())
        {
            var error = Assert.Catch(() => Nip44.Decrypt(vector.GetProperty("payload").GetString()!, Hex(vector.GetProperty("conversation_key").GetString()!)));
            Assert.That(error, Is.InstanceOf<FormatException>().Or.InstanceOf<UnknownNip44VersionException>(), vector.GetProperty("note").GetString());
            count++;
        }
        Assert.That(count, Is.GreaterThan(100));
    }

    [Test]
    public void The_real_client_refuses_what_is_not_an_integration_key()
    {
        var gateway = new SattlemeGateway(new SdkLogger(Mock.Of<IServiceScopeFactory>()));

        Assert.Throws<InvalidKeyException>(() => gateway.ClientOf(""));
        Assert.Throws<InvalidKeyException>(() => gateway.ClientOf("sattleme_ck_damaged"));
        Assert.Throws<InvalidKeyException>(() => gateway.ClientOf(SdkData.KEY.Replace("qEVej", "qEVe", StringComparison.Ordinal)));
    }

    [Test]
    public void The_real_client_is_made_once_per_key_without_the_network_and_never_shows_the_key()
    {
        var gateway = new SattlemeGateway(new SdkLogger(Mock.Of<IServiceScopeFactory>()));
        var client = gateway.ClientOf(SdkData.KEY);

        Assert.That(gateway.ClientOf(SdkData.KEY), Is.SameAs(client));
        Assert.That(client.ToString(), Does.Not.Contain(SdkData.KEY).And.Not.Contain(SdkData.KEY[12..40]));
    }

    [Test]
    public async Task The_SDK_s_warnings_go_to_nopCommerce_s_log_and_never_fail_a_payment()
    {
        var logger = new Mock<Nop.Services.Logging.ILogger>();
        var written = new TaskCompletionSource<string>();
        logger.Setup(l => l.WarningAsync(It.IsAny<string>(), It.IsAny<Exception>(), It.IsAny<Core.Domain.Customers.Customer>()))
            .Callback((string message, Exception _, Core.Domain.Customers.Customer _) => written.TrySetResult(message))
            .Returns(Task.CompletedTask);
        var services = new ServiceCollection().AddSingleton(logger.Object).BuildServiceProvider();
        var sdkLogger = new SdkLogger(services.GetRequiredService<IServiceScopeFactory>());

        Microsoft.Extensions.Logging.LoggerExtensions.LogWarning(sdkLogger, "a rate source failed: {Source}", "coingecko");
        Microsoft.Extensions.Logging.LoggerExtensions.LogDebug(sdkLogger, "not written");

        Assert.That(await written.Task.WaitAsync(TimeSpan.FromSeconds(10)), Is.EqualTo("a rate source failed: coingecko"));
        Assert.That(sdkLogger.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Debug), Is.False);
        var broken = new SdkLogger(Mock.Of<IServiceScopeFactory>());
        Assert.DoesNotThrow(() => Microsoft.Extensions.Logging.LoggerExtensions.LogWarning(broken, "dropped"));
    }
}