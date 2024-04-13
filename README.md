# CryMatch
**Simple**, **scalable** and **high performance** game matchmaking system

Developed as an **alternative to OpenMatch** because most games don't need that complexity.

<div style="color:#fc8c03">
<b>Disclaimer:</b> This project is still in early development. I developed this for my master's thesis and have not had any time to develop all planned features or write a proper wiki. As I get more time and begin integrating it more into my own projects, I will also expand it more. Feel free to contribute.
</div>

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
           

## System scheme
TODO

## Usage
TODO

### Ticket structure
TODO

## Performance
I tested matchmaking performance for worst-case tickets where each has 4 requirements and 1 affinity and all are matchable. Most usage scenarios will not have this many things to compare.

### DigitalOcean measurements
The following are measurements conducted on various **DigitalOcean** servers and on my local PC:

<div style="text-align:center">
    <img src="https://cryshana.me/f/mUAShc3R64WX.png" style="max-width: 100%;max-width:700px;display:inline-block;"></img>
</div>

`D8_1CPU` stands for `Droplet 8$/month` with `1 CPU` etc.

The `3950X_16CPU` stands for my PC with 3950X Ryzen CPU with 16 cores.

### Local PC measurements
Here is a graph for matching 5v5 and 1v1 tickets using my PC with 16 cores:

<div style="text-align:center">
    <img src="https://cryshana.me/f/bRVKRYyIBsC5.png" style="max-width: 100%;max-width:700px;display:inline-block;"></img>
</div>
