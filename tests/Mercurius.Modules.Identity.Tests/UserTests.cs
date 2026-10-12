using Mercurius.Modules.Identity.DTOs;
using Mercurius.Modules.Identity;
using Mercurius.Modules.Identity.Contracts;
using Mercurius.LAN.API.Data;
using Mercurius.Modules.Shared;
using Mercurius.Modules.Shared.Exceptions;
using Mercurius.LAN.API.Migrations;
using Mercurius.Modules.Shared.Search;
using Mercurius.Modules.Identity.Services.Auth0;
using Mercurius.Modules.Identity.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using System.Reflection;
using System.Text.Json;

namespace Mercurius.Modules.Identity.Tests;

public class UserTests
{
    [Fact]
    public void UserModel_EnforcesUserIdentityUniqueness()
    {
        using var dbContext = CreateDbContext();
        var entityType = dbContext.Model.FindEntityType(typeof(User));

        Assert.NotNull(entityType);
        Assert.Equal([nameof(User.Id)], entityType.FindPrimaryKey()?.Properties.Select(property => property.Name).ToArray());

        var indexes = entityType.GetIndexes().ToList();
        AssertUniqueIndex(indexes, nameof(User.Auth0UserId), filter: null);
        AssertUniqueIndex(indexes, nameof(User.Username), "\"Username\" IS NOT NULL AND \"IsDeleted\" = false");
        AssertUniqueIndex(indexes, nameof(User.NormalizedUsername), "\"NormalizedUsername\" IS NOT NULL AND \"IsDeleted\" = false");
        AssertUniqueIndex(indexes, nameof(User.Email), "\"Email\" IS NOT NULL AND \"IsDeleted\" = false");
    }

    [Fact]
    public void UserIdentityUniquenessMigration_AddsFilteredUsernameAndEmailIndexes()
    {
        var migration = new UserIdentityUniqueness();
        var operations = migration.UpOperations.ToList();

        Assert.Contains(operations, operation =>
            operation is CreateIndexOperation createIndex &&
            createIndex.Table == "Users" &&
            createIndex.Name == "IX_Users_Username" &&
            createIndex.IsUnique &&
            createIndex.Filter == "\"Username\" IS NOT NULL AND \"IsDeleted\" = false");
        Assert.Contains(operations, operation =>
            operation is CreateIndexOperation createIndex &&
            createIndex.Table == "Users" &&
            createIndex.Name == "IX_Users_Email" &&
            createIndex.IsUnique &&
            createIndex.Filter == "\"Email\" IS NOT NULL AND \"IsDeleted\" = false");
    }

    [Fact]
    public async Task GetPublicUsernamesByIdsAsync_ReturnsOnlyCompleteActiveUsers()
    {
        await using var dbContext = CreateDbContext();
        var publicUser = CreateStoredUser("auth0|public", "public@example.com", "public-user");
        var deletedUser = CreateStoredUser("auth0|deleted", "deleted@example.com", "deleted-user");
        deletedUser.IsDeleted = true;
        var incompleteUser = CreateStoredUser("auth0|incomplete", "incomplete@example.com", "incomplete-user");
        incompleteUser.Lastname = null;
        dbContext.Users.AddRange(publicUser, deletedUser, incompleteUser);
        await dbContext.SaveChangesAsync();

        var module = new IdentityModuleFacade(dbContext, new RecordingAuth0ManagementService(new Auth0ProfileSnapshot(null, null, false)));
        var usernames = await module.GetPublicUsernamesByIdsAsync(
            [new UserId(publicUser.Id), new UserId(deletedUser.Id), new UserId(incompleteUser.Id)]);

        Assert.Equal(new Dictionary<UserId, string>
        {
            [new UserId(publicUser.Id)] = "public-user"
        }, usernames);
    }

    [Fact]
    public async Task IsAdminUserAsync_RequiresAnActiveLocalAccountAndCurrentAuth0Role()
    {
        await using var dbContext = CreateDbContext();
        var activeUser = CreateStoredUser("auth0|active-admin", "active@example.com", "active-admin");
        var deletedUser = CreateStoredUser("auth0|deleted-admin", "deleted@example.com", "deleted-admin");
        deletedUser.IsDeleted = true;
        dbContext.Users.AddRange(activeUser, deletedUser);
        await dbContext.SaveChangesAsync();
        var auth0 = new RecordingAuth0ManagementService(new Auth0ProfileSnapshot(null, null, false))
        {
            HasAdminRole = true
        };
        var module = new IdentityModuleFacade(dbContext, auth0);

        Assert.True(await module.IsAdminUserAsync(new UserId(activeUser.Id)));
        Assert.False(await module.IsAdminUserAsync(new UserId(deletedUser.Id)));
        Assert.False(await module.IsAdminUserAsync(new UserId(Guid.NewGuid())));
        Assert.Equal(1, auth0.HasAdminRoleCallCount);
        Assert.Equal(activeUser.Auth0UserId, auth0.LastHasAdminRoleAuth0UserId);
    }

    [Fact]
    public async Task GetAdminUsersAsync_ReturnsOnlyActiveLocalMatchesForAuth0AdminMembership()
    {
        await using var dbContext = CreateDbContext();
        var matchingAdmin = CreateStoredUser("auth0|matching-admin", "matching@example.com", "AdminOne");
        var otherAdmin = CreateStoredUser("auth0|other-admin", "other@example.com", "OtherAdmin");
        var deletedAdmin = CreateStoredUser("auth0|deleted-admin", "deleted@example.com", "AdminDeleted");
        deletedAdmin.IsDeleted = true;
        dbContext.Users.AddRange(matchingAdmin, otherAdmin, deletedAdmin);
        await dbContext.SaveChangesAsync();
        var auth0 = new RecordingAuth0ManagementService(new Auth0ProfileSnapshot(null, null, false))
        {
            AdminAuth0UserIds = [matchingAdmin.Auth0UserId, otherAdmin.Auth0UserId, deletedAdmin.Auth0UserId]
        };
        var module = new IdentityModuleFacade(dbContext, auth0);

        var admins = await module.GetAdminUsersAsync("adminone", 100);

        var admin = Assert.Single(admins);
        Assert.Equal(new UserId(matchingAdmin.Id), admin.Id);
        Assert.Equal("AdminOne", admin.Username);
        Assert.Equal(1, auth0.GetAdminUserIdsCallCount);
    }

    [Fact]
    public async Task GetAdminUsersAsync_ReturnsStablePagesAndFiltersBeforePaging()
    {
        await using var dbContext = CreateDbContext();
        var users = Enumerable.Range(0, 55)
            .Select(index => CreateStoredUser($"auth0|admin-{index:D2}", $"admin-{index:D2}@example.com", $"Admin{index:D2}"))
            .ToArray();
        dbContext.Users.AddRange(users);
        await dbContext.SaveChangesAsync();
        var auth0 = new RecordingAuth0ManagementService(new Auth0ProfileSnapshot(null, null, false))
        {
            AdminAuth0UserIds = users.Select(user => user.Auth0UserId).ToArray()
        };
        var module = new IdentityModuleFacade(dbContext, auth0);

        var firstPage = await module.GetAdminUsersAsync(string.Empty, 50, CancellationToken.None, 1);
        var secondPage = await module.GetAdminUsersAsync(string.Empty, 50, CancellationToken.None, 2);
        var filteredPage = await module.GetAdminUsersAsync("admin5", 2, CancellationToken.None, 2);
        var overflowPage = await module.GetAdminUsersAsync(string.Empty, 50, CancellationToken.None, int.MaxValue);

        Assert.Equal(50, firstPage.Count);
        Assert.Equal(5, secondPage.Count);
        Assert.Equal(users.Select(user => new UserId(user.Id)), firstPage.Concat(secondPage).Select(user => user.Id));
        Assert.Equal(["Admin52", "Admin53"], filteredPage.Select(user => user.Username));
        Assert.Empty(overflowPage);
    }

    [Fact]
    public async Task GetAdminUsersAsync_IncludesIncompleteProfilesWhenUnfilteredAndKeepsFilteredQuerySafe()
    {
        await using var dbContext = CreateDbContext();
        var completeAdmin = CreateStoredUser("auth0|complete-admin", "complete@example.com", "CompleteAdmin");
        var noUsernameAdmin = CreateStoredUser("auth0|no-username-admin", "no-username@example.com");
        noUsernameAdmin.Username = null;
        noUsernameAdmin.NormalizedUsername = null;
        noUsernameAdmin.Firstname = null;
        noUsernameAdmin.Lastname = null;
        var blankUsernameAdmin = CreateStoredUser("auth0|blank-username-admin", "blank@example.com");
        blankUsernameAdmin.Username = "   ";
        blankUsernameAdmin.NormalizedUsername = "   ";
        var nonAdmin = CreateStoredUser("auth0|non-admin", "non-admin@example.com", "NonAdmin");
        var deletedAdmin = CreateStoredUser("auth0|deleted-admin", "deleted@example.com", "DeletedAdmin");
        deletedAdmin.IsDeleted = true;
        dbContext.Users.AddRange(completeAdmin, noUsernameAdmin, blankUsernameAdmin, nonAdmin, deletedAdmin);
        await dbContext.SaveChangesAsync();
        var auth0 = new RecordingAuth0ManagementService(new Auth0ProfileSnapshot(null, null, false))
        {
            AdminAuth0UserIds =
            [
                completeAdmin.Auth0UserId,
                noUsernameAdmin.Auth0UserId,
                blankUsernameAdmin.Auth0UserId,
                deletedAdmin.Auth0UserId
            ]
        };
        var module = new IdentityModuleFacade(dbContext, auth0);

        var unfiltered = await module.GetAdminUsersAsync(string.Empty, 50);
        var filtered = await module.GetAdminUsersAsync("complete", 50);

        Assert.Equal(
            new[] { completeAdmin.Id, noUsernameAdmin.Id, blankUsernameAdmin.Id }.OrderBy(id => id),
            unfiltered.Select(user => user.Id.Value).OrderBy(id => id));
        Assert.Equal("Player One", unfiltered.Single(user => user.Id.Value == completeAdmin.Id).DisplayName);
        Assert.Equal("Incomplete profile", unfiltered.Single(user => user.Id.Value == noUsernameAdmin.Id).DisplayName);
        Assert.Equal(completeAdmin.Id, Assert.Single(filtered).Id.Value);
    }

    [Fact]
    public void UpdateLocalProfile_UpdatesAppOwnedProfileFields()
    {
        var user = new User
        {
            Username = "user",
            NormalizedUsername = "user",
            Firstname = "OldFirst",
            Lastname = "OldLast",
            Email = "old@test.com"
        };
        var updatedAtUtc = DateTime.UtcNow;

        user.UpdateLocalProfile("NewUser", "newuser", "NewFirst", "NewLast", "discord", "steam", "riot", updatedAtUtc);

        Assert.Equal("NewUser", user.Username);
        Assert.Equal("newuser", user.NormalizedUsername);
        Assert.Equal("NewFirst", user.Firstname);
        Assert.Equal("NewLast", user.Lastname);
        Assert.Equal("old@test.com", user.Email);
        Assert.Equal("discord", user.DiscordId);
        Assert.Equal("steam", user.SteamId);
        Assert.Equal("riot", user.RiotId);
        Assert.Equal("NewFirst NewLast", user.DisplayName);
        Assert.Equal(updatedAtUtc, user.UpdatedAtUtc);
    }

    [Fact]
    public void SyncAuth0Profile_UpdatesEmailSnapshotOnly()
    {
        var user = new User
        {
            Username = "user",
            Email = "old@test.com",
            EmailVerified = false
        };
        var updatedAtUtc = DateTime.UtcNow;

        user.SyncAuth0Profile("new@test.com", true, updatedAtUtc);

        Assert.Equal("new@test.com", user.Email);
        Assert.True(user.EmailVerified);
        Assert.Equal("user", user.Username);
        Assert.Equal(updatedAtUtc, user.UpdatedAtUtc);
    }

    [Fact]
    public void GetUserDTO_MapsProfileFieldsAndDisplayName()
    {
        var user = new User
        {
            Id = Guid.NewGuid(),
            Auth0UserId = "auth0|123",
            Username = "PlayerOne",
            NormalizedUsername = "playerone",
            Firstname = "Player",
            Lastname = "One",
            Email = "playerone@test.com",
            EmailVerified = true,
            DiscordId = "discord-1",
            SteamId = "steam-1",
            RiotId = "riot-1"
        };

        var dto = new GetUserDTO(user);

        Assert.Equal(user.Id, dto.Id);
        Assert.Equal("PlayerOne", dto.Username);
        Assert.Equal("Player", dto.Firstname);
        Assert.Equal("One", dto.Lastname);
        Assert.Equal("playerone@test.com", dto.Email);
        Assert.True(dto.EmailVerified);
        Assert.Equal("discord-1", dto.DiscordId);
        Assert.Equal("steam-1", dto.SteamId);
        Assert.Equal("riot-1", dto.RiotId);
        Assert.Equal("Player One", dto.DisplayName);
    }

    [Fact]
    public void PublicUserProfileDTO_DoesNotExposePrivateFields()
    {
        var properties = typeof(PublicUserProfileDTO)
            .GetProperties()
            .Select(property => property.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.DoesNotContain("Email", properties);
        Assert.DoesNotContain("EmailVerified", properties);
        Assert.DoesNotContain("Auth0UserId", properties);
        Assert.DoesNotContain("IsDeleted", properties);
        Assert.DoesNotContain("CreatedAtUtc", properties);
        Assert.DoesNotContain("UpdatedAtUtc", properties);
        Assert.Equal(["Username"], properties);
    }

    [Fact]
    public void UserSearchResultDTO_DoesNotExposePrivateFields()
    {
        var properties = typeof(UserSearchResultDTO)
            .GetProperties()
            .Select(property => property.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Contains("Id", properties);
        Assert.Contains("Type", properties);
        Assert.Contains("Username", properties);
        Assert.Contains("DisplayLabel", properties);
        Assert.Contains("SupportingText", properties);
        Assert.DoesNotContain("Email", properties);
        Assert.DoesNotContain("EmailVerified", properties);
        Assert.DoesNotContain("Auth0UserId", properties);
        Assert.DoesNotContain("IsDeleted", properties);
        Assert.DoesNotContain("Firstname", properties);
        Assert.DoesNotContain("Lastname", properties);
        Assert.DoesNotContain("DiscordId", properties);
        Assert.DoesNotContain("SteamId", properties);
        Assert.DoesNotContain("RiotId", properties);
        Assert.DoesNotContain("CreatedAtUtc", properties);
        Assert.DoesNotContain("UpdatedAtUtc", properties);
    }

    [Fact]
    public void DisplayName_FallsBackToUsername_WhenNameIsMissing()
    {
        var user = new User
        {
            Username = "fallback-user",
            Firstname = "",
            Lastname = " "
        };

        Assert.Equal("fallback-user", user.DisplayName);
    }

    [Fact]
    public void IsComplete_RequiresProfileFieldsAndActiveAccount()
    {
        var user = new User
        {
            Username = "playerone",
            NormalizedUsername = "playerone",
            Firstname = "Player",
            Lastname = "One"
        };

        Assert.True(user.IsComplete);

        user.IsDeleted = true;

        Assert.False(user.IsComplete);
    }

    [Theory]
    [InlineData(" PlayerOne ", "playerone")]
    [InlineData("ADMIN", "admin")]
    public void NormalizeUsername_TrimsAndLowercasesInvariantly(string username, string expected)
    {
        Assert.Equal(expected, UserProfileValidationHelper.NormalizeUsername(username));
    }

    [Theory]
    [InlineData("ab")]
    [InlineData("user-name")]
    [InlineData("thisusernameiswaytoolongtobeaccepted")]
    [InlineData("0123456789abcdef0123456789abcdef")]
    public void IsUsernameValid_RejectsInvalidUsernames(string username)
    {
        Assert.False(UserProfileValidationHelper.IsUsernameValid(username));
    }

    [Fact]
    public void IsReservedUsername_RejectsReservedNames()
    {
        Assert.True(UserProfileValidationHelper.IsReservedUsername(" Admin "));
    }

    [Fact]
    public void Anonymize_ClearsPersonalFieldsAndKeepsHistoricalIdentity()
    {
        var user = new User
        {
            Id = Guid.NewGuid(),
            Auth0UserId = "auth0|123",
            Username = "PlayerOne",
            NormalizedUsername = "playerone",
            Firstname = "Player",
            Lastname = "One",
            Email = "playerone@test.com",
            EmailVerified = true,
            DiscordId = "discord-1",
            SteamId = "steam-1",
            RiotId = "riot-1"
        };
        var deletedAtUtc = DateTime.UtcNow;

        user.Anonymize(deletedAtUtc);

        Assert.True(user.IsDeleted);
        Assert.Equal("auth0|123", user.Auth0UserId);
        Assert.StartsWith("deleted-user-", user.Username);
        Assert.Equal(user.Username, user.NormalizedUsername);
        Assert.Null(user.Firstname);
        Assert.Null(user.Lastname);
        Assert.Null(user.Email);
        Assert.False(user.EmailVerified);
        Assert.Null(user.DiscordId);
        Assert.Null(user.SteamId);
        Assert.Null(user.RiotId);
        Assert.Equal(deletedAtUtc, user.DeletedAtUtc);
    }

    [Fact]
    public async Task CreateUserAsync_ForwardsValidProfileRequest()
    {
        var inner = new RecordingUserService();
        var service = new UserValidationService(inner);
        var request = new CreateUserProfileRequest
        {
            Auth0UserId = "auth0|123",
            Username = "ValidUser",
            Firstname = "Player",
            Lastname = "One",
            Email = "playerone@test.com",
            DiscordId = "discord-1"
        };

        var result = await service.CreateUserAsync(request);

        Assert.Same(inner.CreatedUser, result);
        Assert.NotNull(inner.LastCreateRequest);
        Assert.Equal("ValidUser", inner.LastCreateRequest!.Username);
        Assert.Equal("playerone@test.com", inner.LastCreateRequest.Email);
    }

    [Fact]
    public async Task CreateUserAsync_RejectsMissingAuth0UserId()
    {
        var service = new UserValidationService(new RecordingUserService());
        var request = new CreateUserProfileRequest
        {
            Auth0UserId = "",
            Username = "ValidUser",
            Firstname = "Player",
            Lastname = "One"
        };

        var exception = await Assert.ThrowsAsync<ValidationException>(() => service.CreateUserAsync(request));

        Assert.Contains("Auth0 user id", exception.Message);
    }

    [Fact]
    public async Task CreateCurrentUserAsync_ForwardsSubjectAndProfileRequest()
    {
        var inner = new RecordingUserService();
        var service = new UserValidationService(inner);
        var request = new CompleteUserProfileRequest
        {
            Username = "ValidUser",
            Firstname = "Player",
            Lastname = "One"
        };

        var result = await service.CreateCurrentUserAsync("auth0|123", request);

        Assert.Same(inner.CreatedUser, result);
        Assert.Equal("auth0|123", inner.LastCreateCurrentSubject);
        Assert.Same(request, inner.LastCreateCurrentRequest);
    }

    [Fact]
    public async Task CompleteProfileAsync_ForwardsSubjectAndProfileRequest()
    {
        var inner = new RecordingUserService();
        var service = new UserValidationService(inner);
        var request = new CompleteUserProfileRequest
        {
            Username = "ValidUser",
            Firstname = "Player",
            Lastname = "One"
        };

        var result = await service.CompleteProfileAsync("auth0|123", request);

        Assert.Same(inner.CreatedUser, result);
        Assert.Equal("auth0|123", inner.LastCompleteSubject);
        Assert.Same(request, inner.LastCompleteRequest);
    }

    [Fact]
    public async Task UpdateUserAsync_RejectsInvalidIdentityPayload()
    {
        var service = new UserValidationService(new RecordingUserService());
        var request = new UpdateUserProfileRequest
        {
            Username = "x",
            Firstname = "Player",
            Lastname = "One"
        };

        var exception = await Assert.ThrowsAsync<ValidationException>(() => service.UpdateUserAsync(Guid.NewGuid(), request));

        Assert.Contains("Username", exception.Message);
    }

    [Fact]
    public async Task UpdateCurrentUserAsync_ForwardsValidProfileRequest()
    {
        var inner = new RecordingUserService();
        var service = new UserValidationService(inner);
        var request = new UpdateUserProfileRequest
        {
            Username = "ValidUser",
            Firstname = "Player",
            Lastname = "One"
        };

        var result = await service.UpdateCurrentUserAsync("auth0|123", request);

        Assert.Same(inner.CreatedUser, result);
        Assert.Equal("auth0|123", inner.LastUpdateCurrentSubject);
        Assert.Same(request, inner.LastUpdateCurrentRequest);
    }

    [Fact]
    public async Task GetPublicUserProfileByUsernameAsync_ForwardsNormalizedUsername()
    {
        var inner = new RecordingUserService();
        var service = new UserValidationService(inner);

        var result = await service.GetPublicUserProfileByUsernameAsync(" ValidUser ");

        Assert.Same(inner.PublicUser, result);
        Assert.Equal("validuser", inner.LastPublicProfileUsername);
    }

    [Fact]
    public async Task GetPublicUserProfileByUsernameAsync_RejectsInvalidUsername()
    {
        var service = new UserValidationService(new RecordingUserService());

        var exception = await Assert.ThrowsAsync<ValidationException>(() =>
            service.GetPublicUserProfileByUsernameAsync("x"));

        Assert.Contains("Username must be 3-32 alphanumeric characters.", exception.Message);
    }

    [Fact]
    public async Task SearchUsersAsync_ForwardsNormalizedQueryCursorAndPageSize()
    {
        var inner = new RecordingUserService();
        var service = new UserValidationService(inner);

        var result = await service.SearchUsersAsync(" Alpha ", "cursor-1", 7);

        Assert.Same(inner.UserSearchResponse, result);
        Assert.Equal("alpha", inner.LastUserSearchQuery);
        Assert.Equal("cursor-1", inner.LastUserSearchCursor);
        Assert.Equal(7, inner.LastUserSearchPageSize);
    }

    [Fact]
    public async Task SearchUsersAsync_RejectsOverlongQuery()
    {
        var service = new UserValidationService(new RecordingUserService());
        var query = new string('a', SearchRequestLimits.MaximumQueryLength + 1);

        var exception = await Assert.ThrowsAsync<ValidationException>(() =>
            service.SearchUsersAsync(query, cursor: null, pageSize: 10));

        Assert.Contains($"Query cannot exceed {SearchRequestLimits.MaximumQueryLength} characters.", exception.Message);
    }

    [Fact]
    public async Task GetAdminUsersAsync_ForwardsQueryPageAndPageSize()
    {
        var inner = new RecordingUserService();
        var service = new UserValidationService(inner);

        var result = await service.GetAdminUsersAsync(" Alpha ", page: 2, pageSize: 7);

        Assert.Same(inner.AdminUserOptions, result);
        Assert.Equal(" Alpha ", inner.LastAdminQuery);
        Assert.Equal(2, inner.LastAdminPage);
        Assert.Equal(7, inner.LastAdminPageSize);
    }

    [Fact]
    public async Task GetAdminUsersAsync_RejectsOverlongQuery()
    {
        var service = new UserValidationService(new RecordingUserService());
        var query = new string('a', SearchRequestLimits.MaximumQueryLength + 1);

        var exception = await Assert.ThrowsAsync<ValidationException>(() =>
            service.GetAdminUsersAsync(query, page: 1, pageSize: 10));

        Assert.Contains($"Query cannot exceed {SearchRequestLimits.MaximumQueryLength} characters.", exception.Message);
    }

    [Fact]
    public async Task DeleteUserByIdAsync_RejectsEmptyIds()
    {
        var service = new UserValidationService(new RecordingUserService());

        var exception = await Assert.ThrowsAsync<ValidationException>(() => service.DeleteUserByIdAsync(Guid.Empty));

        Assert.Equal("Invalid user ID.", exception.Message);
    }

    [Fact]
    public async Task DeleteUserAsync_AnonymizesExistingUserByUsername()
    {
        await using var dbContext = CreateDbContext();
        var user = CreateStoredUser("auth0|delete-me", "delete-me@example.com");
        user.Username = "DeleteMe";
        user.NormalizedUsername = "deleteme";
        dbContext.Users.Add(user);
        await dbContext.SaveChangesAsync();

        var service = CreateUserService(dbContext, new RecordingAuth0ManagementService(new Auth0ProfileSnapshot(null, null, false)));

        await service.DeleteUserAsync("DELETEME");

        var storedUser = await dbContext.Users.SingleAsync(u => u.Id == user.Id);
        Assert.True(storedUser.IsDeleted);
        Assert.StartsWith("deleted-user-", storedUser.Username);
        Assert.Null(storedUser.Email);
    }

    [Fact]
    public async Task DeleteUserAsync_ThrowsNotFound_WhenUsernameDoesNotExist()
    {
        await using var dbContext = CreateDbContext();
        var service = CreateUserService(dbContext, new RecordingAuth0ManagementService(new Auth0ProfileSnapshot(null, null, false)));

        var exception = await Assert.ThrowsAsync<NotFoundException>(() => service.DeleteUserAsync("missinguser"));

        Assert.Contains("missinguser", exception.Message);
    }

    [Fact]
    public async Task GetCurrentUserAsync_ReturnsExistingUserWithoutCreatingOrSyncingAuth0Profile()
    {
        await using var dbContext = CreateDbContext();
        var user = CreateStoredUser("auth0|current", "stored@example.com");
        dbContext.Users.Add(user);
        await dbContext.SaveChangesAsync();

        var auth0ManagementService = new RecordingAuth0ManagementService(
            new Auth0ProfileSnapshot("fresh@example.com", true, true));
        var service = CreateUserService(dbContext, auth0ManagementService);

        var response = await service.GetCurrentUserAsync("auth0|current");

        Assert.True(response.IsComplete);
        Assert.Equal(user.Id, response.User?.Id);
        Assert.Equal("stored@example.com", response.Email);
        Assert.False(response.EmailVerified);
        Assert.Equal(0, auth0ManagementService.GetUserProfileCallCount);

        var storedUser = await dbContext.Users.SingleAsync(u => u.Id == user.Id);
        Assert.Equal("stored@example.com", storedUser.Email);
        Assert.False(storedUser.EmailVerified);
    }

    [Fact]
    public async Task GetCurrentUserAsync_ThrowsNotFoundWithoutCreatingUser_WhenCurrentUserMissing()
    {
        await using var dbContext = CreateDbContext();
        var auth0ManagementService = new RecordingAuth0ManagementService(
            new Auth0ProfileSnapshot("fresh@example.com", true, true));
        var service = CreateUserService(dbContext, auth0ManagementService);

        await Assert.ThrowsAsync<NotFoundException>(() => service.GetCurrentUserAsync("auth0|missing"));

        Assert.Equal(0, auth0ManagementService.GetUserProfileCallCount);
        Assert.Empty(await dbContext.Users.ToListAsync());
    }

    [Fact]
    public async Task CreateCurrentUserAsync_CreatesMissingCurrentUserFromAuthenticatedSubject()
    {
        await using var dbContext = CreateDbContext();
        var auth0ManagementService = new RecordingAuth0ManagementService(
            new Auth0ProfileSnapshot("fresh@example.com", true, true));
        var service = CreateUserService(dbContext, auth0ManagementService);
        var request = new CompleteUserProfileRequest
        {
            Username = "NewPlayer",
            Firstname = "New",
            Lastname = "Player",
            DiscordId = "discord-1"
        };

        var created = await service.CreateCurrentUserAsync(" auth0|new ", request);

        Assert.Equal("NewPlayer", created.Username);
        Assert.Equal("fresh@example.com", created.Email);
        Assert.True(created.EmailVerified);
        Assert.Equal(1, auth0ManagementService.GetUserProfileCallCount);
        Assert.Equal("auth0|new", auth0ManagementService.LastGetUserProfileAuth0UserId);

        var storedUser = await dbContext.Users.SingleAsync();
        Assert.Equal("auth0|new", storedUser.Auth0UserId);
        Assert.Equal("NewPlayer", storedUser.Username);
        Assert.Equal("newplayer", storedUser.NormalizedUsername);
        Assert.Equal("fresh@example.com", storedUser.Email);
        Assert.Equal("discord-1", storedUser.DiscordId);
    }

    [Fact]
    public async Task CreateCurrentUserAsync_RejectsExistingCurrentUserWithoutOverwritingProfile()
    {
        await using var dbContext = CreateDbContext();
        var user = CreateStoredUser("auth0|existing", "stored@example.com");
        dbContext.Users.Add(user);
        await dbContext.SaveChangesAsync();

        var auth0ManagementService = new RecordingAuth0ManagementService(
            new Auth0ProfileSnapshot("fresh@example.com", true, true));
        var service = CreateUserService(dbContext, auth0ManagementService);
        var request = new CompleteUserProfileRequest
        {
            Username = "OtherUser",
            Firstname = "Other",
            Lastname = "User"
        };

        var exception = await Assert.ThrowsAsync<ValidationException>(() =>
            service.CreateCurrentUserAsync("auth0|existing", request));

        Assert.Equal("Current user profile already exists.", exception.Message);
        Assert.Equal(0, auth0ManagementService.GetUserProfileCallCount);

        var storedUser = await dbContext.Users.SingleAsync(u => u.Id == user.Id);
        Assert.Equal("PlayerOne", storedUser.Username);
        Assert.Equal("stored@example.com", storedUser.Email);
    }

    [Fact]
    public async Task CompleteProfileAsync_UpdatesExistingCurrentUserWithoutCallingAuth0Profile()
    {
        await using var dbContext = CreateDbContext();
        var user = CreateStoredUser("auth0|existing", "stored@example.com");
        user.Username = null;
        user.NormalizedUsername = null;
        user.Firstname = null;
        user.Lastname = null;
        dbContext.Users.Add(user);
        await dbContext.SaveChangesAsync();

        var auth0ManagementService = new RecordingAuth0ManagementService(
            new Auth0ProfileSnapshot("fresh@example.com", true, true));
        var service = CreateUserService(dbContext, auth0ManagementService);
        var request = new CompleteUserProfileRequest
        {
            Username = "CompletedUser",
            Firstname = "Completed",
            Lastname = "User",
            SteamId = "steam-1"
        };

        var completed = await service.CompleteProfileAsync("auth0|existing", request);

        Assert.Equal("CompletedUser", completed.Username);
        Assert.Equal("stored@example.com", completed.Email);
        Assert.False(completed.EmailVerified);
        Assert.Equal(0, auth0ManagementService.GetUserProfileCallCount);

        var storedUser = await dbContext.Users.SingleAsync(u => u.Id == user.Id);
        Assert.Equal("CompletedUser", storedUser.Username);
        Assert.Equal("completeduser", storedUser.NormalizedUsername);
        Assert.Equal("Completed", storedUser.Firstname);
        Assert.Equal("User", storedUser.Lastname);
        Assert.Equal("steam-1", storedUser.SteamId);
        Assert.Equal("stored@example.com", storedUser.Email);
    }

    [Fact]
    public async Task CompleteProfileAsync_ThrowsNotFoundWithoutCreatingUser_WhenCurrentUserMissing()
    {
        await using var dbContext = CreateDbContext();
        var auth0ManagementService = new RecordingAuth0ManagementService(
            new Auth0ProfileSnapshot("fresh@example.com", true, true));
        var service = CreateUserService(dbContext, auth0ManagementService);
        var request = new CompleteUserProfileRequest
        {
            Username = "ValidUser",
            Firstname = "Valid",
            Lastname = "User"
        };

        await Assert.ThrowsAsync<NotFoundException>(() => service.CompleteProfileAsync("auth0|missing", request));

        Assert.Equal(0, auth0ManagementService.GetUserProfileCallCount);
        Assert.Empty(await dbContext.Users.ToListAsync());
    }

    [Fact]
    public async Task UpdateCurrentUserAsync_ThrowsNotFoundWithoutCreatingUser_WhenCurrentUserMissing()
    {
        await using var dbContext = CreateDbContext();
        var service = CreateUserService(
            dbContext,
            new RecordingAuth0ManagementService(new Auth0ProfileSnapshot("fresh@example.com", true, true)));
        var request = new UpdateUserProfileRequest
        {
            Username = "ValidUser",
            Firstname = "Valid",
            Lastname = "User"
        };

        await Assert.ThrowsAsync<NotFoundException>(() => service.UpdateCurrentUserAsync("auth0|missing", request));

        Assert.Empty(await dbContext.Users.ToListAsync());
    }

    [Fact]
    public void Auth0LinkedUser_StoresExternalUserId()
    {
        var user = new User
        {
            Auth0UserId = "auth0|social-user",
            Username = "social-user",
            Email = "social@test.com"
        };

        Assert.Equal("auth0|social-user", user.Auth0UserId);
    }

    [Fact]
    public async Task SendPasswordResetEmailAsync_DoesNotTriggerReset_ForSocialOnlyIdentity()
    {
        await using var dbContext = CreateDbContext();
        var user = CreateStoredUser("google-oauth2|123", "shared@example.com");
        dbContext.Users.Add(user);
        await dbContext.SaveChangesAsync();

        var auth0ManagementService = new RecordingAuth0ManagementService(
            new Auth0ProfileSnapshot("shared@example.com", true, false));
        var service = CreateUserService(dbContext, auth0ManagementService);

        var response = await service.SendPasswordResetEmailAsync(user.Auth0UserId);

        Assert.Equal("If password reset is available for this account, a password reset email has been sent.", response.Message);
        Assert.Null(auth0ManagementService.LastPasswordResetEmail);
    }

    [Fact]
    public async Task SendPasswordResetEmailAsync_TriggersReset_ForDatabaseIdentity()
    {
        await using var dbContext = CreateDbContext();
        var user = CreateStoredUser("auth0|123", "shared@example.com");
        dbContext.Users.Add(user);
        await dbContext.SaveChangesAsync();

        var auth0ManagementService = new RecordingAuth0ManagementService(
            new Auth0ProfileSnapshot("shared@example.com", true, true));
        var service = CreateUserService(dbContext, auth0ManagementService);

        await service.SendPasswordResetEmailAsync(user.Auth0UserId);

        Assert.Equal("shared@example.com", auth0ManagementService.LastPasswordResetEmail);
    }

    [Fact]
    public async Task GetPublicUserProfileByUsernameAsync_ReturnsUsernameOnly()
    {
        await using var dbContext = CreateDbContext();
        var user = CreateStoredUser("auth0|123", "public@example.com");
        user.DiscordId = "discord-1";
        user.SteamId = "steam-1";
        user.RiotId = "riot-1";
        dbContext.Users.Add(user);
        await dbContext.SaveChangesAsync();

        var service = CreateUserService(
            dbContext,
            new RecordingAuth0ManagementService(new Auth0ProfileSnapshot("public@example.com", true, true)));

        var profile = await service.GetPublicUserProfileByUsernameAsync("playerone");

        Assert.Equal("PlayerOne", profile.Username);
        Assert.Equal(["Username"], typeof(PublicUserProfileDTO).GetProperties().Select(property => property.Name));
    }

    [Fact]
    public async Task GetUserByUsernameAsync_ReturnsDetailedProfileForAdminWorkflow()
    {
        await using var dbContext = CreateDbContext();
        var user = CreateStoredUser("auth0|123", "private@example.com");
        user.DiscordId = "discord-1";
        dbContext.Users.Add(user);
        await dbContext.SaveChangesAsync();

        var service = CreateUserService(
            dbContext,
            new RecordingAuth0ManagementService(new Auth0ProfileSnapshot("private@example.com", true, true)));

        var profile = await service.GetUserByUsernameAsync("PLAYERONE");

        Assert.Equal(user.Id, profile.Id);
        Assert.Equal(user.Email, profile.Email);
        Assert.Equal(user.Firstname, profile.Firstname);
        Assert.Equal("discord-1", profile.DiscordId);
    }

    [Fact]
    public async Task GetPublicUserProfileByUsernameAsync_IsCaseInsensitive()
    {
        await using var dbContext = CreateDbContext();
        dbContext.Users.Add(CreateStoredUser("auth0|123", "public@example.com"));
        await dbContext.SaveChangesAsync();

        var service = CreateUserService(
            dbContext,
            new RecordingAuth0ManagementService(new Auth0ProfileSnapshot("public@example.com", true, true)));

        var profile = await service.GetPublicUserProfileByUsernameAsync("PlAyErOnE");

        Assert.Equal("PlayerOne", profile.Username);
    }

    [Fact]
    public async Task GetPublicUserProfileByUsernameAsync_ThrowsNotFound_ForMissingUser()
    {
        await using var dbContext = CreateDbContext();
        var service = CreateUserService(
            dbContext,
            new RecordingAuth0ManagementService(new Auth0ProfileSnapshot("public@example.com", true, true)));

        await Assert.ThrowsAsync<NotFoundException>(() =>
            service.GetPublicUserProfileByUsernameAsync("playerone"));
    }

    [Fact]
    public async Task GetPublicUserProfileByUsernameAsync_ThrowsNotFound_ForDeletedUser()
    {
        await using var dbContext = CreateDbContext();
        var user = CreateStoredUser("auth0|123", "public@example.com");
        user.Anonymize(DateTime.UtcNow);
        dbContext.Users.Add(user);
        await dbContext.SaveChangesAsync();

        var service = CreateUserService(
            dbContext,
            new RecordingAuth0ManagementService(new Auth0ProfileSnapshot("public@example.com", true, true)));

        await Assert.ThrowsAsync<NotFoundException>(() =>
            service.GetPublicUserProfileByUsernameAsync("playerone"));
    }

    [Fact]
    public async Task GetPublicUserProfileByUsernameAsync_ThrowsNotFound_ForIncompleteUser()
    {
        await using var dbContext = CreateDbContext();
        var user = CreateStoredUser("auth0|123", "public@example.com");
        user.Lastname = null;
        dbContext.Users.Add(user);
        await dbContext.SaveChangesAsync();

        var service = CreateUserService(
            dbContext,
            new RecordingAuth0ManagementService(new Auth0ProfileSnapshot("public@example.com", true, true)));

        await Assert.ThrowsAsync<NotFoundException>(() =>
            service.GetPublicUserProfileByUsernameAsync("playerone"));
    }

    [Fact]
    public async Task SearchUsersAsync_ReturnsBoundedPrivacySafeMatches_InDeterministicOrder()
    {
        await using var dbContext = CreateDbContext();
        var exact = CreateStoredUser("auth0|exact", "exact@example.com", "Alpha");
        var prefix = CreateStoredUser("auth0|prefix", "prefix@example.com", "AlphaBeta");
        var contains = CreateStoredUser("auth0|contains", "contains@example.com", "BetaAlpha");
        var deleted = CreateStoredUser("auth0|deleted", "deleted@example.com", "AlphaDeleted");
        deleted.Anonymize(DateTime.UtcNow);
        var missingUsername = CreateStoredUser("auth0|missing-username", "missing-username@example.com", "AlphaMissing");
        missingUsername.Username = null;
        var missingNormalizedUsername = CreateStoredUser("auth0|missing-normalized", "missing-normalized@example.com", "AlphaMissingNormalized");
        missingNormalizedUsername.NormalizedUsername = null;
        dbContext.Users.AddRange(contains, prefix, exact, deleted, missingUsername, missingNormalizedUsername);
        await dbContext.SaveChangesAsync();

        var service = CreateUserService(
            dbContext,
            new RecordingAuth0ManagementService(new Auth0ProfileSnapshot("public@example.com", true, true)));

        var response = await service.SearchUsersAsync("  ALPHA  ", cursor: null, pageSize: 2);

        Assert.True(response.HasMore);
        Assert.NotNull(response.NextCursor);
        Assert.Collection(response.Results,
            first =>
            {
                Assert.Equal(exact.Id, first.Id);
                Assert.Equal("user", first.Type);
                Assert.Equal("Alpha", first.Username);
                Assert.Equal("Alpha", first.DisplayLabel);
                Assert.Equal("User", first.SupportingText);
            },
            second =>
            {
                Assert.Equal(prefix.Id, second.Id);
                Assert.Equal("AlphaBeta", second.Username);
            });

        var json = JsonSerializer.Serialize(response, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Contains("\"results\"", json);
        Assert.Contains("\"nextCursor\"", json);
        Assert.Contains("\"hasMore\"", json);
        Assert.DoesNotContain("email", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("auth0", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("deleted", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("firstname", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("lastname", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetAllUsersAsync_ReturnsDeterministicBoundedPagesAndHandlesOverflow()
    {
        await using var dbContext = CreateDbContext();
        var alpha = CreateStoredUser("auth0|alpha", "alpha@example.com", "Alpha");
        var bravoFirst = CreateStoredUser("auth0|bravo-first", "bravo-first@example.com", "Bravo");
        var bravoSecond = CreateStoredUser("auth0|bravo-second", "bravo-second@example.com", "Bravo");
        bravoFirst.Id = Guid.Parse("00000000-0000-0000-0000-000000000001");
        bravoSecond.Id = Guid.Parse("00000000-0000-0000-0000-000000000002");
        dbContext.Users.AddRange(bravoSecond, alpha, bravoFirst);
        await dbContext.SaveChangesAsync();

        var service = CreateUserService(
            dbContext,
            new RecordingAuth0ManagementService(new Auth0ProfileSnapshot("public@example.com", true, true)));

        var firstPage = await service.GetAllUsersAsync(page: 1, pageSize: 2);
        var secondPage = await service.GetAllUsersAsync(page: 2, pageSize: 2);
        var overflowPage = await service.GetAllUsersAsync(page: int.MaxValue, pageSize: 50);
        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();

        Assert.Equal([alpha.Id, bravoFirst.Id], firstPage.Select(user => user.Id));
        Assert.Equal([bravoSecond.Id], secondPage.Select(user => user.Id));
        Assert.Empty(overflowPage);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.GetAllUsersAsync(page: int.MaxValue, pageSize: 50, cancellationSource.Token));
    }

    [Fact]
    public async Task GetAdminUsersAsync_DefaultsPageAndCapsPageSizeAndNormalizesQuery()
    {
        await using var dbContext = CreateDbContext();
        var identityModule = new RecordingIdentityModule();
        var service = CreateUserService(
            dbContext,
            new RecordingAuth0ManagementService(new Auth0ProfileSnapshot(null, null, false)),
            identityModule);

        await service.GetAdminUsersAsync(null, page: null, pageSize: null);
        await service.GetAdminUsersAsync("  ALPHA  ", page: 3, pageSize: 51);

        Assert.Equal([("", 1, 20), ("alpha", 3, 50)], identityModule.AdminCalls);
    }

    [Fact]
    public async Task GetAdminUsersAsync_MapsPickerOptionsWithIncompleteProfileFallback()
    {
        await using var dbContext = CreateDbContext();
        var identityModule = new RecordingIdentityModule
        {
            AdminUsers =
            [
                new UserProfileSummary(new UserId(Guid.NewGuid()), "picker-user", "Picker Person", false, null, null, null),
                new UserProfileSummary(new UserId(Guid.NewGuid()), "   ", "Incomplete profile", false, null, null, null)
            ]
        };
        var service = CreateUserService(
            dbContext,
            new RecordingAuth0ManagementService(new Auth0ProfileSnapshot(null, null, false)),
            identityModule);

        var options = await service.GetAdminUsersAsync(null, page: 1, pageSize: 20);

        Assert.Equal(2, options.Count);
        Assert.Equal("picker-user", options[0].Username);
        Assert.Equal("Picker Person", options[0].DisplayName);
        Assert.Equal("Incomplete profile", options[1].Username);
    }

    [Fact]
    public async Task SearchUsersAsync_DoesNotRequireFirstOrLastName()
    {
        await using var dbContext = CreateDbContext();
        var user = CreateStoredUser("auth0|username-only", "username-only@example.com", "UsernameOnly");
        user.Firstname = null;
        user.Lastname = null;
        dbContext.Users.Add(user);
        await dbContext.SaveChangesAsync();

        var service = CreateUserService(
            dbContext,
            new RecordingAuth0ManagementService(new Auth0ProfileSnapshot("public@example.com", true, true)));

        var response = await service.SearchUsersAsync("username", cursor: null, pageSize: 10);

        var result = Assert.Single(response.Results);
        Assert.Equal(user.Id, result.Id);
        Assert.Equal("UsernameOnly", result.Username);
    }

    [Fact]
    public async Task SearchUsersAsync_ReturnsEmptyResults_ForShortQueries()
    {
        await using var dbContext = CreateDbContext();
        dbContext.Users.Add(CreateStoredUser("auth0|short", "short@example.com", "Alpha"));
        await dbContext.SaveChangesAsync();

        var service = CreateUserService(
            dbContext,
            new RecordingAuth0ManagementService(new Auth0ProfileSnapshot("public@example.com", true, true)));

        var response = await service.SearchUsersAsync("al", cursor: null, pageSize: 10);

        Assert.Empty(response.Results);
        Assert.False(response.HasMore);
        Assert.Null(response.NextCursor);
    }

    [Fact]
    public async Task SearchUsersAsync_SupportsCursorContinuation()
    {
        await using var dbContext = CreateDbContext();
        dbContext.Users.AddRange(
            CreateStoredUser("auth0|alpha", "alpha@example.com", "Alpha"),
            CreateStoredUser("auth0|alphaa", "alphaa@example.com", "Alphaa"),
            CreateStoredUser("auth0|alphab", "alphab@example.com", "Alphab"));
        await dbContext.SaveChangesAsync();

        var service = CreateUserService(
            dbContext,
            new RecordingAuth0ManagementService(new Auth0ProfileSnapshot("public@example.com", true, true)));

        var page1 = await service.SearchUsersAsync("alpha", cursor: null, pageSize: 2);
        var page2 = await service.SearchUsersAsync("alpha", page1.NextCursor, pageSize: 2);

        Assert.True(page1.HasMore);
        Assert.NotNull(page1.NextCursor);
        Assert.False(page2.HasMore);
        Assert.Null(page2.NextCursor);
        Assert.Equal(["Alpha", "Alphaa", "Alphab"], page1.Results.Concat(page2.Results).Select(result => result.Username));
    }

    [Fact]
    public void SearchUsersAsync_QueryAndKeysetCursor_TranslateForPostgreSql()
    {
        var options = new DbContextOptionsBuilder<MercuriusDBContext>()
            .UseNpgsql("Host=localhost;Database=translation-only")
            .Options;
        using var dbContext = new MercuriusDBContext(options);
        var service = CreateUserService(
            dbContext,
            new RecordingAuth0ManagementService(new Auth0ProfileSnapshot("public@example.com", true, true)));

        var buildQuery = typeof(UserService).GetMethod("BuildPagedUserSearchQuery", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var cursorType = typeof(UserService).GetNestedType("UserSearchCursor", BindingFlags.NonPublic)!;
        var cursorId = Guid.NewGuid();

        var cursor = Activator.CreateInstance(cursorType, "alpha", 1, "alphab", cursorId)!;
        var query = (IQueryable)buildQuery.Invoke(service, ["alpha", cursor, 3])!;
        var sql = query.ToQueryString();

        Assert.Contains("LIKE", sql);
        Assert.Contains("\"NormalizedUsername\"", sql);
        Assert.Contains("\"Id\"", sql);
        Assert.Contains("ORDER BY", sql);
        Assert.Contains("LIMIT", sql);
        Assert.DoesNotContain("CAST", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("::text", sql, StringComparison.OrdinalIgnoreCase);
    }

    private static MercuriusDBContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<MercuriusDBContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        return new MercuriusDBContext(options);
    }

    private static UserService CreateUserService(
        MercuriusDBContext dbContext,
        IAuth0ManagementService auth0ManagementService,
        IIdentityModule? identityModule = null) =>
        new(dbContext, auth0ManagementService, identityModule ?? new RecordingIdentityModule());

    private static void AssertUniqueIndex(IEnumerable<IIndex> indexes, string propertyName, string? filter)
    {
        var index = Assert.Single(indexes, candidate =>
            candidate.Properties.Select(property => property.Name).SequenceEqual([propertyName]));

        Assert.True(index.IsUnique);
        Assert.Equal(filter, index.GetFilter());
    }

    private static User CreateStoredUser(string auth0UserId, string email, string username = "PlayerOne")
    {
        var now = DateTime.UtcNow;
        return new User
        {
            Id = Guid.NewGuid(),
            Auth0UserId = auth0UserId,
            Username = username,
            NormalizedUsername = username.ToLowerInvariant(),
            Firstname = "Player",
            Lastname = "One",
            Email = email,
            EmailVerified = false,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
    }

    private sealed class RecordingUserService : IUserService
    {
        public CreateUserProfileRequest? LastCreateRequest { get; private set; }
        public string? LastCreateCurrentSubject { get; private set; }
        public CompleteUserProfileRequest? LastCreateCurrentRequest { get; private set; }
        public string? LastCompleteSubject { get; private set; }
        public CompleteUserProfileRequest? LastCompleteRequest { get; private set; }
        public string? LastUpdateCurrentSubject { get; private set; }
        public UpdateUserProfileRequest? LastUpdateCurrentRequest { get; private set; }
        public string? LastPublicProfileUsername { get; private set; }
        public string? LastUserByUsername { get; private set; }
        public string? LastUserSearchQuery { get; private set; }
        public string? LastUserSearchCursor { get; private set; }
        public int LastUserSearchPageSize { get; private set; }
        public string? LastAdminQuery { get; private set; }
        public int? LastAdminPage { get; private set; }
        public int? LastAdminPageSize { get; private set; }
        public IReadOnlyList<AdminUserOptionDTO> AdminUserOptions { get; } =
            [new AdminUserOptionDTO(Guid.NewGuid(), "ValidUser", "ValidUser")];
        public GetUserDTO CreatedUser { get; } = new(new User
        {
            Id = Guid.NewGuid(),
            Auth0UserId = "auth0|123",
            Username = "ValidUser",
            NormalizedUsername = "validuser",
            Firstname = "Player",
            Lastname = "One",
            Email = "playerone@test.com"
        });
        public PublicUserProfileDTO PublicUser { get; } = new()
        {
            Username = "ValidUser"
        };
        public UserSearchResponseDTO UserSearchResponse { get; } = new()
        {
            Results =
            [
                new()
                {
                    Id = Guid.NewGuid(),
                    Type = "user",
                    Username = "ValidUser",
                    DisplayLabel = "ValidUser",
                    SupportingText = "User"
                }
            ],
            HasMore = false
        };

        public Task<UserSearchResponseDTO> SearchUsersAsync(
            string? query,
            string? cursor,
            int pageSize,
            CancellationToken cancellationToken = default)
        {
            LastUserSearchQuery = query;
            LastUserSearchCursor = cursor;
            LastUserSearchPageSize = pageSize;
            return Task.FromResult(UserSearchResponse);
        }

        public Task<GetUserDTO> CreateUserAsync(CreateUserProfileRequest request)
        {
            LastCreateRequest = request;
            return Task.FromResult(CreatedUser);
        }

        public Task<GetUserDTO> CreateCurrentUserAsync(string auth0UserId, CompleteUserProfileRequest request)
        {
            LastCreateCurrentSubject = auth0UserId;
            LastCreateCurrentRequest = request;
            return Task.FromResult(CreatedUser);
        }

        public Task<GetUserDTO> CompleteProfileAsync(string auth0UserId, CompleteUserProfileRequest request)
        {
            LastCompleteSubject = auth0UserId;
            LastCompleteRequest = request;
            return Task.FromResult(CreatedUser);
        }

        public Task<CurrentUserProfileResponse> GetCurrentUserAsync(string auth0UserId)
        {
            return Task.FromResult(new CurrentUserProfileResponse(true, CreatedUser));
        }

        public Task<PublicUserProfileDTO> GetPublicUserProfileByUsernameAsync(string username)
        {
            LastPublicProfileUsername = username;
            return Task.FromResult(PublicUser);
        }

        public Task<GetUserDTO> UpdateCurrentUserAsync(string auth0UserId, UpdateUserProfileRequest request)
        {
            LastUpdateCurrentSubject = auth0UserId;
            LastUpdateCurrentRequest = request;
            return Task.FromResult(CreatedUser);
        }

        public Task<UsernameAvailabilityResponse> CheckUsernameAvailabilityAsync(string auth0UserId, string username)
        {
            return Task.FromResult(new UsernameAvailabilityResponse { Username = username, IsAvailable = true });
        }

        public Task<UserActionResponse> ResendVerificationEmailAsync(string auth0UserId)
        {
            return Task.FromResult(new UserActionResponse("verification"));
        }

        public Task<UserActionResponse> SendPasswordResetEmailAsync(string auth0UserId)
        {
            return Task.FromResult(new UserActionResponse("password"));
        }

        public Task<UserActionResponse> AnonymizeCurrentUserAsync(string auth0UserId)
        {
            return Task.FromResult(new UserActionResponse("deleted"));
        }

        public Task DeleteUserAsync(string username) => Task.CompletedTask;
        public Task DeleteUserByIdAsync(Guid id) => Task.CompletedTask;
        public Task<IReadOnlyList<GetUserDTO>> GetAllUsersAsync(int page, int pageSize, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<GetUserDTO>>([]);
        public Task<GetUserDTO> GetUserByIdAsync(Guid id) => Task.FromResult(CreatedUser);
        public Task<GetUserDTO> GetUserByUsernameAsync(string username)
        {
            LastUserByUsername = username;
            return Task.FromResult(CreatedUser);
        }
        public Task<GetUserDTO> UpdateUserAsync(Guid id, UpdateUserProfileRequest request) => Task.FromResult(CreatedUser);
        public Task<IReadOnlyList<AdminUserOptionDTO>> GetAdminUsersAsync(
            string? query,
            int? page,
            int? pageSize,
            CancellationToken cancellationToken = default)
        {
            LastAdminQuery = query;
            LastAdminPage = page;
            LastAdminPageSize = pageSize;
            return Task.FromResult(AdminUserOptions);
        }
    }

    private sealed class RecordingIdentityModule : IIdentityModule
    {
        public IReadOnlyList<UserProfileSummary> AdminUsers { get; init; } = [];
        public List<(string Query, int Page, int PageSize)> AdminCalls { get; } = [];

        public Task<IReadOnlyList<UserProfileSummary>> GetAdminUsersAsync(
            string normalizedQuery,
            int pageSize,
            CancellationToken cancellationToken = default,
            int page = 1)
        {
            AdminCalls.Add((normalizedQuery, page, pageSize));
            return Task.FromResult(AdminUsers);
        }

        public Task<UserProfileSummary?> GetUserProfileAsync(UserId userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<UserProfileSummary?> GetUserProfileByAuth0IdAsync(string auth0UserId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<PublicUserProfileSummary?> GetPublicProfileByUsernameAsync(string username, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<PublicUserProfileSummary?> GetPublicProfileByIdAsync(UserId userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyDictionary<UserId, UserProfileSummary>> GetUsersByIdsAsync(IReadOnlyCollection<UserId> userIds, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyDictionary<UserId, string>> GetPublicUsernamesByIdsAsync(IReadOnlyCollection<UserId> userIds, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<PublicUserSearchDocument>> GetPublicUserSearchDocumentsPageAsync(UserId? afterId, int pageSize, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> IsAdminUserAsync(UserId userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class RecordingAuth0ManagementService : IAuth0ManagementService
    {
        private readonly Auth0ProfileSnapshot _profileSnapshot;

        public RecordingAuth0ManagementService(Auth0ProfileSnapshot profileSnapshot)
        {
            _profileSnapshot = profileSnapshot;
        }

        public string? LastPasswordResetEmail { get; private set; }
        public string? LastGetUserProfileAuth0UserId { get; private set; }
        public int GetUserProfileCallCount { get; private set; }
        public bool HasAdminRole { get; init; }
        public int HasAdminRoleCallCount { get; private set; }
        public string? LastHasAdminRoleAuth0UserId { get; private set; }
        public IReadOnlyList<string> AdminAuth0UserIds { get; init; } = [];
        public int GetAdminUserIdsCallCount { get; private set; }

        public Task<Auth0ProfileSnapshot> GetUserProfileAsync(string auth0UserId, CancellationToken cancellationToken = default)
        {
            LastGetUserProfileAuth0UserId = auth0UserId;
            GetUserProfileCallCount++;
            return Task.FromResult(_profileSnapshot);
        }

        public Task<bool> HasAdminRoleAsync(string auth0UserId, CancellationToken cancellationToken = default)
        {
            LastHasAdminRoleAuth0UserId = auth0UserId;
            HasAdminRoleCallCount++;
            return Task.FromResult(HasAdminRole);
        }

        public Task<IReadOnlyList<string>> GetAdminUserIdsAsync(CancellationToken cancellationToken = default)
        {
            GetAdminUserIdsCallCount++;
            return Task.FromResult(AdminAuth0UserIds);
        }

        public Task SendVerificationEmailAsync(string auth0UserId, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task SendPasswordResetEmailAsync(string email, CancellationToken cancellationToken = default)
        {
            LastPasswordResetEmail = email;
            return Task.CompletedTask;
        }
    }
}
