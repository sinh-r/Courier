using Courier.Core.Auth;

namespace Courier.Core.Tests;

/// <summary>
/// Saved auth profiles in <c>auth/*.auth.yaml</c> — committed and shared, and never carrying a
/// secret. ENT-02.
/// </summary>
public sealed class AuthProfileStoreTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("courier-auth-store-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void A_profile_survives_a_save_and_load_round_trip()
    {
        var profile = new AuthProfile
        {
            Name = "entra-qa",
            Kind = AuthKind.EntraClientCredentials,
            Tenant = "contoso.onmicrosoft.com",
            ClientId = "11111111-1111-1111-1111-111111111111",
            Scopes = ["api://orders/.default"],
            SecretRef = "9f8b7c6d5e4f3a2b1c0d9e8f7a6b5c4d",
        };

        AuthProfileStore.Save(_folder, profile);
        var restored = AuthProfileStore.Load(_folder, "entra-qa");

        Assert.NotNull(restored);
        Assert.Equal(AuthKind.EntraClientCredentials, restored!.Kind);
        Assert.Equal("contoso.onmicrosoft.com", restored.Tenant);
        Assert.Equal(profile.SecretRef, restored.SecretRef);
        Assert.Contains("api://orders/.default", restored.Scopes);
    }

    [Fact]
    public void Saving_never_writes_a_secret_value_to_disk()
    {
        var profile = new AuthProfile { Name = "svc", Kind = AuthKind.Bearer, SecretRef = "abc123" };
        AuthProfileStore.Save(_folder, profile);

        var yaml = File.ReadAllText(Path.Combine(_folder, "auth", "svc.auth.yaml"));

        // secretRef is a non-secret pointer into the credential store, not the secret itself, so it
        // is fine — and expected — in the file. What must never appear is an actual token or password.
        Assert.DoesNotContain("hunter2", yaml, StringComparison.Ordinal);
        Assert.Contains("secretRef", yaml, StringComparison.Ordinal);
    }

    [Fact]
    public void Loading_a_name_with_no_file_returns_null_not_a_default_profile()
    {
        Assert.Null(AuthProfileStore.Load(_folder, "does-not-exist"));
    }

    [Fact]
    public void ListNames_is_empty_for_a_collection_with_no_auth_folder_yet()
    {
        Assert.Empty(AuthProfileStore.ListNames(_folder));
    }

    [Fact]
    public void ListNames_and_LoadAll_see_every_saved_profile()
    {
        AuthProfileStore.Save(_folder, new AuthProfile { Name = "b-profile", Kind = AuthKind.Basic });
        AuthProfileStore.Save(_folder, new AuthProfile { Name = "a-profile", Kind = AuthKind.Bearer });

        Assert.Equal(["a-profile", "b-profile"], AuthProfileStore.ListNames(_folder));
        Assert.Equal(2, AuthProfileStore.LoadAll(_folder).Count);
    }

    [Fact]
    public void Renaming_moves_the_file_but_leaves_the_secret_ref_untouched()
    {
        var profile = new AuthProfile { Name = "old-name", Kind = AuthKind.Bearer, SecretRef = "keep-me" };
        AuthProfileStore.Save(_folder, profile);

        AuthProfileStore.Rename(_folder, "old-name", "new-name");

        Assert.Null(AuthProfileStore.Load(_folder, "old-name"));
        var renamed = AuthProfileStore.Load(_folder, "new-name");
        Assert.NotNull(renamed);
        Assert.Equal("keep-me", renamed!.SecretRef);
    }

    [Fact]
    public void Deleting_a_profile_removes_its_file()
    {
        AuthProfileStore.Save(_folder, new AuthProfile { Name = "temp", Kind = AuthKind.Bearer });
        AuthProfileStore.Delete(_folder, "temp");

        Assert.Null(AuthProfileStore.Load(_folder, "temp"));
    }
}
