# CryMatch
**Simple**, **scalable** and **high performance** game matchmaking system

Developed as an **alternative to OpenMatch** because most games don't need that complexity.

---

<b>Disclaimer:</b> This project is still in early development. I developed this for my master's thesis and have not had any time to develop all planned features or write a proper wiki. As I get more time and begin integrating it more into my own projects, I will also expand it more. Feel free to contribute.

## Features
- **Very easy to set up**
    - just run the executable
    - no Kubernetes required
    - no Docker required
    - no Redis required
    - self-contained, runs everywhere

- **Very easy to configure**
    - configure everything in single file `config.json`
    - no specific programming language knowledge required
    - most behaviour is defined via tickets

- **Easy integration and flexibility**
    - **gRPC** for communication, works with any language of your choice (that supports gRPC)
    - ticket itself contains 99% of matchmaking rules
        - can be configured with any language of your choice
        - no need to write error-prone and difficult-to-test logic in Go for every matchmaking component 
    - can extend functionality with native libraries
        - can use any language that supports this, like C#, Rust, C/C++ etc
        - used mainly if you want to override how matches are made by default

- **Vertical & Horizontal scalability**
    - process scales vertically by utilizing all available system threads
    - can scale matchmakers horizontally by using **Redis** if needed

- **High performance**
    - can be AOT or JIT compiled
        - AOT has faster startup and lower latency
        - JIT sometimes performs better at matching (does more optimizations for specific hardware)
    - handles **busiest scenarios** (most games, however, have issues with **too few** players)
    - performance scales with CPU cores: (this is **worst-case** with 4 requirements and 1 affinity per ticket and all are matchable)
        - a 1-core server can match 10 000 tickets in less than 14 seconds, but 20 000 in ~1 minute.
        - a 2-core server can match 20 000 tickets in less than 32 seconds.
        - a 4-core server can match 20 000 tickets in less than 12 seconds.
        - a 16-core server can match 40 000 tickets in less than 6 seconds.
    - more common scenario is even faster because there is less to compare (this is for matching on skill rating only)
        - a 16-core server can match 50 000 tickets in less than 4 seconds.
    - For comparison, `Counter Strike 2` has *at most* 30 000 players in queue at once per region and gamemode (this is greedily speculated based on steam stats, regions and game modes)
           
- **Supports most common matchmaking scenarios**
    - matching based on similar or disimilar value (such as skill rating, reputation score, latency, ...)
    - matching based on requirements (such as acceptable skill range, maps, language, role ...)
    - ability to prioritize certain tickets
    - ability to prioritize based on ticket age (older unmatched tickets have higher priority by default)
    - ability to prioritize based on affinity value (such as latency similarity being more important than skill rating similarity)
    - custom match sizes (can configure custom sizes like 2,3,4,5...)
        - system only matches tickets into a group, the actual separation into "2v2/5v5/..." should be made by your frontend
    - can override how matches are formed by extending functionality with a native library plugin
        - usually used when you want the matched group to have certain properties as a whole, such as:
            - matched group containing players whose preferred roles complement each other, this is quite specialized behaviour
            - this can also be emulated by utilizing similar value matching

## System structure
The matchmaking system is comprised of different components:
- **Director** (assigns tickets to matchmakers, **can be only one** in a distributed system)
    - your frontend mainly communicates with the Director. Any new tickets or cancellations should be sent to the Director.
- **Matchmaker** (handles tickets and matches them, **can be many** matchmakers in a distributed system)
    - each matchmaker runs multiple matching functions in parallel (is configured, by default 2)
    - each matching function handles only one ticket pool (unlike OpenMatch where same ticket pool can be distributed across many match functions) - this decision was made because one matching function is sufficient for even busiest of ticket pools, but am still planning on adding OpenMatch behaviour as a feature in the future
- **State** (holds the system state, ticket pool configurations, tickets and their assignments)
    - for Standalone operation uses `internal process memory`
    - for Distributed operation uses `Redis`

<img src="https://cryshana.me/f/gvLFHkPykjXe.png" style="max-width: 100%;max-width:700px;"></img>

## Usage
TODO

### Ticket structure
Please check the `ticket.proto` protobuf file for all supported ticket fields and what they do.

Most importantly, your frontend will utilize the following fields:
- `matchmaking_pool_id` specifies which ticket pool the ticket should be assigned to (this should be separated per region and game mode)
- `state` holds state values which are used by requirements. Positioning is important because these values are referenced by their position in the array.
- `requirements` are self-explanatory. They reference values stored in the `state` by 0-based index.
- `affinities` are values used for checking similarity/disimilarity. They hold the value themselves and don't reference the `state`. They are compared to other tickets' affinities based on the same position in the affinities array.
- `max_age_seconds` is self-explanatory.

Other fields are less important or irrelevant for frontend, often only used internally for tracking purposes.

## Performance
I tested matchmaking performance for worst-case tickets where each has 4 requirements and 1 affinity and all are matchable. Most usage scenarios will not have this many things to compare.

### DigitalOcean measurements
The following are measurements conducted on various **DigitalOcean** servers and on my local PC:

<img src="https://cryshana.me/f/mUAShc3R64WX.png" style="max-width: 100%;max-width:700px;"></img>

`D8_1CPU` stands for `Droplet 8$/month` with `1 CPU` etc.

The `3950X_16CPU` stands for my PC with 3950X Ryzen CPU with 16 cores.

### Local PC measurements
Here is a graph for matching 5v5 and 1v1 tickets using my PC with 16 cores:

 <img src="https://cryshana.me/f/bRVKRYyIBsC5.png" style="max-width: 100%;max-width:700px;"></img>

## Testing
The `CryMatchTests` project contains all relevant tests for the system. It's a bit rough but it tests all it needs to.

In order to run the tests you will need to set up an accessible Redis instance on `localhost`. On Windows I used the Linux Subsystem to set up Redis. Should be set up without any password on default port. 

The tests will automatically try to connect to `localhost` with default Redis port as is defined in `StateFixture.cs` file. If you have your Redis instance elsewhere, you can always adjust the Redis connection string in this file.

## Possible features/improvements (TODO)
These are possible features/improvements that I may or may not do in the future:

- **Dynamic ticket gather time** (HIGH PRIORITY) that adjusts based on traffic. This should make the system react better to low traffic scenarios which are common with smaller games and lengthen the gathering time to gather more quality tickets.
- **Improve cleaner of submitted set** (MEDIUM PRIORITY). At the moment the submitted set can contain orphan tickets in cases when state is modified externally or system is reset, etc... We want to ensure proper state at all times.
- **Overhaul of expiry system** (MEDIUM PRIORITY). In-memory state currently uses tasks for expiring entries which may be wasting system resources when system is really busy and a lot of tasks are spawned. Could instead handle this differently, more similar to how Redis handles it - by setting an expiry date and just checking it lazily and at an interval. Because there can be a large number of keys, only a portion of them is checked randomly and depending on how many of them were expired, this is repeated.
- **Web Interface** (MEDIUM PRIORITY) for easy overview of system status and possibly real-time configuration changing
- **Adding more extensible functionality via native libraries** (MEDIUM PRIORITY). Right now the native library extensebility is quite limited. It has the main stuff but more functionality would be wanted, which would offer more flexibility without having to touch the source code itself.
- **Option for multiple matching functions to handle the same ticket pool** (LOW PRIORITY). This behaviour is similar to how OpenMatch works. The problem with this approach is that there will be synchronization issues and many duplicates that have to be removed. OpenMatch uses Evaluator for this. But while it sounds great, it will actually slow down the matching process significantly for smaller ticket pools + it is overkill for even large ticket pools. This would be useful for  niche use cases where people are expecting to match more than 100 000 tickets at once at a reasonable time. (to my knowledge no game on the market needs to match this many people in the same ticket pool)
- **Option to minimize affinity differences across matches** (LOW PRIORITY). At the moment tickets are matched in receiving order. In case we have 4 tickets with skill ratings of 500,1000,1000,2000 -- they are matched like so: (1000,1000) and (500,2000) based on processing order - when it would be more optimal to do (500,1000) and (1000,2000) to minimize skill differences. So instead of 1 good and 1 bad matchup, we could have 2 okayish matchups instead. This depends on your preferences and sometimes it is not desired. Also this can be mitigated by using a HARD LIMIT on affinities. ALSO this problem is minimal for larger number of tickets and only noticable with really small amount of matches. This is why it's low priority.
- **General improvements of ticket handling** (LOW PRIORITY). I don't know if this is necessary at this point, but I feel like I can further make ticket handling more efficient without compromising system integrity. This is more or less an ongoing process

- **MORE TESTS for system failures!!!** Currently I do not test system failures enough, such as:
    - Redis failing at different stages of processing
    - matchmaker failing at different stages of processing

