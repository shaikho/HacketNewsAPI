using HacketNewsAPI.Models;
using Microsoft.Extensions.Caching.Memory;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddMemoryCache();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapGet("/bestStories/{numberOfStories}", new Func<HttpContext, IMemoryCache, Task<IResult>>(async (context, memoryCache) =>
{
    // Create a single instance of HttpClient to be reused for multiple requests
    HttpClient client = new HttpClient();

    int numberOfStories = 10;
    if (context.Request.RouteValues.TryGetValue("numberOfStories", out var numberOfStoriesObj)) // extracting the number of stories from the route
    {
        numberOfStories = int.Parse(numberOfStoriesObj.ToString());
    }

    // Send an HTTP GET request to retrieve the list of best stories
    var response = await client.GetAsync("https://hacker-news.firebaseio.com/v0/beststories.json");

    // Check if the request was successful
    if (response.IsSuccessStatusCode)
    {
        // Read the response content as a string
        var content = await response.Content.ReadAsStringAsync();

        // Parse the JSON response into a list of story IDs
        var storyIds = JsonSerializer.Deserialize<List<int>>(content);

        // Take the specified number of story IDs
        var selectedStoryIds = storyIds.Take(numberOfStories);

        // Create a list to store the retrieved stories
        var stories = new List<Story>();

        foreach (int storyId in selectedStoryIds)
        {
            // Before sending a request, check if the story is in the cache
            var cachedStory = memoryCache.Get<Story>($"Story_{storyId}");
            if (cachedStory != null)
            {
                // Use the cached story instead of sending a request not to use unnecessary bandwidth
                stories.Add(cachedStory);
                selectedStoryIds = selectedStoryIds.Where(id => id != storyId);
            }
        }

        // Send parallel HTTP GET requests to retrieve the details of each story
        var tasks = selectedStoryIds.Select(async id => await client.GetAsync($"https://hacker-news.firebaseio.com/v0/item/{id}.json"));
        var responses = await Task.WhenAll(tasks);

        // Process the responses and extract the story details
        foreach (var innerResponse in responses)
        {
            if (innerResponse.IsSuccessStatusCode)
            {
                var storyContent = await innerResponse.Content.ReadAsStringAsync();
                var story = JsonSerializer.Deserialize<Story>(storyContent);
                stories.Add(story);
                // Store the story in memory cache
                memoryCache.Set($"Story_{story.id}", story, TimeSpan.FromMinutes(20));
            }
        }

        // Return the list of retrieved stories
        stories = stories.OrderByDescending(story => story.score).ToList();
        return Results.Json(stories.ToArray());
    }
    else
    {
        // Handle the case when the request fails
        return Results.StatusCode((int)response.StatusCode);
    }
}));


// a function setup for a fire and forget task to warm up the cache with first 30 stories
async void WarmUpCache()
{
    try
    {
        HttpClient client = new HttpClient();
        var response = await client.GetAsync("http://localhost:5692/bestStories/30");
        if (!response.IsSuccessStatusCode)
        {
            Console.WriteLine("Failed to warm up the cache.");
            return;
        }
        else
        {
            Console.WriteLine("Cache warmed up successfully.");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Failed to warm up the cache. Due to {ex.Message}");
    }
}

// registering the warm-up cache method to be run once app is started
app.Lifetime.ApplicationStarted.Register(() =>
{
    WarmUpCache();
});

app.Run();