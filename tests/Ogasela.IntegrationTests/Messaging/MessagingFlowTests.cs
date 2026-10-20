using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Ogasela.Api.Contracts.Messaging;
using Ogasela.Application.Accounts.GetCurrentUser;
using Ogasela.Application.Messaging.GetBlockedUsers;
using Ogasela.Application.Messaging.GetMessages;
using Ogasela.Application.Messaging.SendMessage;
using Ogasela.Application.Messaging.StartConversation;
using Ogasela.Infrastructure.Promotions.Seed;
using static Ogasela.IntegrationTests.Listings.ListingsTestHelpers;

namespace Ogasela.IntegrationTests.Messaging;

public class MessagingFlowTests : IClassFixture<MessagingApiFactory>
{
    private readonly MessagingApiFactory _factory;

    public MessagingFlowTests(MessagingApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task StartConversation_FromAListing_CreatesAConversationBetweenBuyerAndListingOwner()
    {
        var sellerClient = await RegisterAndVerifySellerAsync(_factory);
        var listing = await CreateAndPublishFreeListingAsync(sellerClient, CategorySeedData.PhonesAndTabletsId);

        var buyerClient = await RegisterSellerAsync(_factory);
        var buyerId = await GetCurrentUserIdAsync(buyerClient);
        var sellerId = await GetCurrentUserIdAsync(sellerClient);

        var response = await buyerClient.PostAsJsonAsync("/api/v1/conversations", new StartConversationRequest(listing.Id));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var conversation = await response.Content.ReadFromJsonAsync<ConversationResponse>(JsonOptions);

        conversation!.ListingId.Should().Be(listing.Id);
        conversation.BuyerId.Should().Be(buyerId);
        conversation.SellerId.Should().Be(sellerId);
    }

    [Fact]
    public async Task StartConversation_CalledTwiceForTheSameBuyerSellerListingTriple_ReturnsTheSameConversation()
    {
        var sellerClient = await RegisterAndVerifySellerAsync(_factory);
        var listing = await CreateAndPublishFreeListingAsync(sellerClient, CategorySeedData.PhonesAndTabletsId);
        var buyerClient = await RegisterSellerAsync(_factory);

        var firstResponse = await buyerClient.PostAsJsonAsync("/api/v1/conversations", new StartConversationRequest(listing.Id));
        var firstConversation = await firstResponse.Content.ReadFromJsonAsync<ConversationResponse>(JsonOptions);

        var secondResponse = await buyerClient.PostAsJsonAsync("/api/v1/conversations", new StartConversationRequest(listing.Id));
        secondResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var secondConversation = await secondResponse.Content.ReadFromJsonAsync<ConversationResponse>(JsonOptions);

        secondConversation!.Id.Should().Be(firstConversation!.Id, "starting a conversation twice must not create a duplicate");
    }

    [Fact]
    public async Task SendMessage_ThenGetMessages_PersistsAndReturnsTheMessage()
    {
        var sellerClient = await RegisterAndVerifySellerAsync(_factory);
        var listing = await CreateAndPublishFreeListingAsync(sellerClient, CategorySeedData.PhonesAndTabletsId);
        var buyerClient = await RegisterSellerAsync(_factory);

        var startResponse = await buyerClient.PostAsJsonAsync("/api/v1/conversations", new StartConversationRequest(listing.Id));
        var conversation = await startResponse.Content.ReadFromJsonAsync<ConversationResponse>(JsonOptions);

        var sendResponse = await buyerClient.PostAsJsonAsync(
            $"/api/v1/conversations/{conversation!.Id}/messages",
            new SendMessageRequest("Is this still available?", null));

        sendResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var sentMessage = await sendResponse.Content.ReadFromJsonAsync<MessageResponse>(JsonOptions);
        sentMessage!.Content.Should().Be("Is this still available?");

        // The seller (the other participant) should see it too.
        var messagesResponse = await sellerClient.GetAsync($"/api/v1/conversations/{conversation.Id}/messages");
        messagesResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var page = await messagesResponse.Content.ReadFromJsonAsync<PagedMessagesResponse>(JsonOptions);

        page!.Items.Should().ContainSingle(m => m.Id == sentMessage.Id && m.Content == "Is this still available?");
    }

    [Fact]
    public async Task SendMessage_AfterTheRecipientBlocksTheSender_IsRejected()
    {
        var sellerClient = await RegisterAndVerifySellerAsync(_factory);
        var listing = await CreateAndPublishFreeListingAsync(sellerClient, CategorySeedData.PhonesAndTabletsId);
        var buyerClient = await RegisterSellerAsync(_factory);
        var buyerId = await GetCurrentUserIdAsync(buyerClient);

        var startResponse = await buyerClient.PostAsJsonAsync("/api/v1/conversations", new StartConversationRequest(listing.Id));
        var conversation = await startResponse.Content.ReadFromJsonAsync<ConversationResponse>(JsonOptions);

        // The buyer can message freely before being blocked.
        var firstSend = await buyerClient.PostAsJsonAsync(
            $"/api/v1/conversations/{conversation!.Id}/messages", new SendMessageRequest("Hello!", null));
        firstSend.StatusCode.Should().Be(HttpStatusCode.OK);

        var blockResponse = await sellerClient.PostAsync($"/api/v1/users/{buyerId}/block", content: null);
        blockResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var secondSend = await buyerClient.PostAsJsonAsync(
            $"/api/v1/conversations/{conversation.Id}/messages", new SendMessageRequest("Are you there?", null));

        secondSend.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task BlockedUser_ShowsInTheBlockedList_AndChatReopensAfterUnblock()
    {
        var sellerClient = await RegisterAndVerifySellerAsync(_factory);
        var listing = await CreateAndPublishFreeListingAsync(sellerClient, CategorySeedData.PhonesAndTabletsId);
        var buyerClient = await RegisterSellerAsync(_factory);
        var buyerId = await GetCurrentUserIdAsync(buyerClient);

        var startResponse = await buyerClient.PostAsJsonAsync("/api/v1/conversations", new StartConversationRequest(listing.Id));
        var conversation = await startResponse.Content.ReadFromJsonAsync<ConversationResponse>(JsonOptions);

        (await sellerClient.PostAsync($"/api/v1/users/{buyerId}/block", content: null)).StatusCode.Should().Be(HttpStatusCode.OK);

        var blocked = await sellerClient.GetFromJsonAsync<List<BlockedUserResponse>>("/api/v1/users/blocked", JsonOptions);
        blocked.Should().ContainSingle(b => b.UserId == buyerId);

        var seen = await sellerClient.GetFromJsonAsync<ConversationResponse>($"/api/v1/conversations/{conversation!.Id}", JsonOptions);
        seen!.IsBlockedByMe.Should().BeTrue();

        // The blocker can't keep messaging someone they blocked either.
        var blockerSend = await sellerClient.PostAsJsonAsync(
            $"/api/v1/conversations/{conversation.Id}/messages", new SendMessageRequest("Hi", null));
        blockerSend.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        (await sellerClient.DeleteAsync($"/api/v1/users/{buyerId}/block")).StatusCode.Should().Be(HttpStatusCode.OK);

        var buyerSend = await buyerClient.PostAsJsonAsync(
            $"/api/v1/conversations/{conversation.Id}/messages", new SendMessageRequest("Still available?", null));
        buyerSend.StatusCode.Should().Be(HttpStatusCode.OK);
        (await sellerClient.GetFromJsonAsync<List<BlockedUserResponse>>("/api/v1/users/blocked", JsonOptions)).Should().BeEmpty();
    }

    private static async Task<Guid> GetCurrentUserIdAsync(HttpClient client)
    {
        var response = await client.GetAsync("/api/v1/me");
        response.EnsureSuccessStatusCode();
        var me = await response.Content.ReadFromJsonAsync<CurrentUserResponse>(JsonOptions);
        return me!.Id;
    }
}
