# Guidance for .NET systems development

Here is some opinionated guidance maybe to save some part of your valuable lifetime.
The following topics focus on: development, architecture and maintainability.

The primary advice after [John Skeet](https://codeblog.jonskeet.uk/2009/11/02/omg-ponies-aka-humanity-epic-fail/):
Just solve your issue in the simplest possible way. Limit the boundaries: what are the requirements?
Don't try to solve meta-level problems of every software engineer ever.
Draft the boundaries even if you're writing a general library: Users can compose multiple small libraries if necessary.
Expect your requirement specifications, if any, could be better, that's life. 
Requirement gathering and technical specification tools have evolved slower than development tools.

General things to consider: How long will your code live, expecting the business to be successful?
(5 years would not be enough to extract all business value, but 20 years would be maintenance and security issues.)
How long would it survive without you? Is it like an old car that needs constant fixing (with regression issues)?
To avoid the source code line count expanding forever: How can your code be removed?

If you write a lot of manual boilerplate structure code or use code generators (like T4, Copilot, or whatever) to generate boilerplate code, 
you can ensure you generate enough to leave your permanent unwanted footprint in history.

Don't build layers and tiers just for a dream of unit testing. If you can quickly build a domain model (your types) to make illegal states unrepresentable, that's good. But don't waste too much time on perfection: You will never capture the full complexity of the real world. Domain models don't age well, because the business focus tends to change, and the roles of types with that. Customers are after the functionality, not your lovely model, patterns and structures. Types are an aid for developers, not a reward of their own.

Version control (mainly Git) tutorial is out of the scope of this document; check for example [learn git branching](https://learngitbranching.js.org/) for that.

Which identity or authentication to use? What database should be used and how to connect?  
These issues in software engineering should already be solved. Spend your time studying/evaluating the existing ones properly
instead of spending the next 5 years of your life re-inventing the wheel by writing your own solution. Your solution would probably be used by you, your best friend and no one else.


# Calling 3rd party services

You will probably need API reference documentation, endpoint address, username, and password for both the test environment/sandbox and production.
Due to contractual reasons, those usually take several days to get, so ask them as soon as possible.

If you are ever bargained for a ballpark figure of development time estimate, it's recommended that you multiply it by PI (3.14).
That's because managers think of calendar time; they don't care about typing speed. When is the minimum viable product implementation in the production environment?
Which usually means email communications, multiple test-environment deployment fixes, etc.
For more detailed project development time estimations in software engineering, there are techniques like Function Point Analysis (out of the scope of this document), and the first question is, what is the estimate for (internal or external, accepted risk levels, etc)?

Many services are billed by call-count, and you should be able to do billing reconciliations: If they send a bill of $1 million, was it that you
leaked unencrypted production credentials to the Internet, or was it that you called the service, or is there an error in their bill?

For this reason, you should save all the responses (hopefully not to the SQL database with full recovery mode logging, nor a log file that needs a BigData solution for parsing).
But it's also helpful to consider saving requests and some webhook requests. Many modern services are asynchronous, so the initial response starts a process, and then the system uses webhook signal callbacks to provide updates on actual processing.

Practical tools for debugging, making and monitoring web traffic calls are [Fiddler](https://www.telerik.com/fiddler/fiddler-classic) / "VSCode Rest Client" / Postman. Development-time webhook responses can be received, e.g., with [https://webhook.site/](https://webhook.site/), Ngrok, or Visual Studio Dev Tunnels.

Fiddler Classic is an HTTP proxy that sits between your computer and the Internet and listens to all traffic that passes through.
To capture and decrypt the HTTPS traffic, you must allow that from options and accept some certificates.
Then, in your code editor, create a breakpoint before the HTTP call, debug (VisualStudio: e.g. "Attach to Process..."), stop into that,
clear all previous traffic from Fiddler, and run the call. You shall see the entire input and output of the web requests.
With Fiddler, you can also modify and reply to the requests, which is helpful in security testing (hacking the app).
Note: If you have Fiddler open when your computer crashes, the internet settings will be saved and messed up. Just re-open Fiddler and close it properly.

There are also local proxies for Android/iOS to debug phone traffic.

However, you don't have control over the schema of the 3rd party service responses. So, their historical value will be somewhat limited,
because the service provider may change their return shape.

Also, if the service is expected to solve a problem, someone will need to know if it is actually fixing the issue.
One commonly forgotten task is to schedule calendar reminders to check that before the next subscription/billing period is started.
This is typically not a developer task, but part of your job is to support your managers in making sound decisions; they may need a push here.


# Caching is the last resort of performance optimization. Caching, .NET ConcurrentDictionary

Maybe you want to save money by not re-executing the same 3rd party service calls repeatedly. (Check the service license to see how caching is allowed.)
If you need an in-memory cache in an environment with multiple threads, don't use a generic List in the .NET environment; it is not thread-safe.
What it means you'll encounter runtime errors that are hard to reproduce, e.g. "Collection was modified; enumeration operation may not execute".
Writing low-level locks is complex and may cause deadlocks. The easy (and widely used) solution is to use `System.Collections.Concurrent.ConcurrentDictionary`.

However, it's a complex collection:

1. Don't use it like IEnumerable. It's only thread-safe if you use specific methods like `GetOrAdd` and `AddOrUpdate`, where it wraps the write function with a lock.
2. You can save tasks (or async/IObservable/lazy resources) to ConcurrentDictionary... however:
3. If you saved a task and it throws an exception to the dictionary, you'll get exceptions when you fetch them!
4. They are expensive to construct, so don't make thousands of them, not one per each type, but a few static ones.
5. Do you ever clean the dictionary, or will it increase memory usage? What if your software runs for a month? .NET has another class supporting expiration, called MemoryCache, but it's not thread-safe on writes.

Cache invalidation is well-known to be a complicated problem.
Like with any .NET dictionary: If you do x["key"], it may throw KeyNotFoundException. To avoid this, use `x.TryGetValue` rather than double-lookup with x.ContainsKey.


# Avoid Regex if possibe

Regular expressions are a problematic way of doing text-parsing with limited operations.
There is a classic joke: Some people, when confronted with a problem, think "I know, I'll use regular expressions." Now they have two problems.

Many times, they are faster and cleaner than creating your own parser. 
In general, string parsing should be a solved issue in software engineering; you shouldn't need to write your own.
Often, .NET already offers enough string functions like `.StartsWith`, so use them.

Regular expressions are based on Chomsky Type 3 grammar, which means they are "whitelisting" what are the allowed characters.
That means trying to parse un-restricted text (like HTML) will eventually fail, as [explained in this classic post](https://stackoverflow.com/a/1732454).

One fundamental issue is that regular expressions are slow and take a lot of time to parse, making them suitable for a `Denial of Service` attack.

If possible, always try to use closed regular expressions rather than open ones, making them like `.StartsWith` or `.EndsWith` and not `.Contains`. 
That way, you can reduce computational complexity significantly.

- `abc` means "target contains abc", `^abc` means "target starts with abc", and `abc$` means "target ends with abc".

If you re-use your regular expressions, you should use them with compiled mode:

 - Create them with `new Regex(pattern)`, capturing that as a static constant and then using that via the `x.Match` method rather than calling `Regex.Match(pattern, ...)`.
 - Consider using `RegexOptions.Compiled` when creating your static constant, but that does increase startup time.

If you have to parse XML, do it via e.g. FSharp.Data.XMLProvider (F#) or Linq-to-Xml (C#).
If you have to parse JSON, do it via e.g. FSharp.Data.JSONProvider (F#) or System.Text.Json.


# Error handling

Generally, on technical errors, fail fast, fail hard. Fix the issues.

Don't throw business cases like "out of money" as technical errors.
Bad performance aside, exceptions also cause random unclear paths between nodes of your program. Besides that, they make debugging .NET harder when you can't just break on each CLR exception.

Avoid using "special case" as a business error (like C# null, or F# [Result-type](https://eiriktsarpalis.wordpress.com/2017/02/19/youre-better-off-using-exceptions/)):
It is better to list your business errors separately, e.g. enum or discriminated union (DU). It's much easier to reason 500 lines of code later about what happened.
No matter how functional goto you create, it's still a goto.

(Null is even worse because it has two responsibilities; it's also an "unassigned state".)

Implement extensive logging using abstractions with proper levels (info/debug/warning/error). 
It's always easier to remove if not needed than trying to hunt a case. 
But avoid logging personal sensitive information (like GDPR data); that'd make your log files sensitive.

If you ever need to read stacktraces, they can be huge. How do you find the originator of an issue? Just expect that the problem is first in your code and second in your company's code. Seek primarily for those.

Note for performance measurements: Logging usually makes things slow, so if you try to measure time with logging,
be aware of how observation can affect the observable. Classic Harry Hill quote applies here:
"It's only when you look at an ant through a magnifying glass on a sunny day that you realize how often they burst into flames."

Note that asynchronous methods (like tasks) in other threads may swallow the exceptions if they are not separately handled.


# Structs vs Classes (ValueTuple, ValueOption, ValueTask and StructAttribute)

The typical way to bundle two items together in .NET is a `Tuple`. A class that bundles data together without needing to create another class.
Tuple is actually a class. But .NET also has a `ValueTuple`, which is a record/struct.
Classes are typically in the heap, and structs are in the stack. To generalize, the heap is "big and slow memory", and the stack is "small and fast memory". Thus, try to prefer the stack for small things that are processed at once.

If your objects are big classes, `Tuple` is just fine. 
But if your objects are some small basic types like `int` or `System.Guid` or `enum`, then it's better to use `ValueTuple`.
The same goes with other value types like `ValueOption`.

Generally, this doesn't matter much unless you try to process 100,000 of these (which is possible in modern software).
A note for people processing such many items with CPU-bound operations: Consider also using TPL `.AsParallel` (or F# Parallel) to utilize more parallelism and cores.

You can always [test benchmark](https://benchmarkdotnet.org/) the differences to learn the balance.
As method parameters, structs are reference-copied and not referenced via memory pointers. The problems start if you have larger struct parameters or a lot of copying; then you begin to lose the benefits; the execution speed will slow down.
You can avoid some of those issues with managed pointers (byref-struct, inref, outref), but maintainability and clear code are often more important than performance.

Most recent .NET versions have a lot of work done to work efficiently with structs because Microsoft is paying the electricity bills of Azure.

If you do F# (FSharp), you probably want to mark small DUs with StructAttribute.
But when writing this text, many of the F# operations on lists work on the `Option`-type. If you have to convert `ValueOption` to `Option`, you have already lost the benefit, so just use `Option`. Active patterns support returning `ValueOption` via the `[<return: Struct>]` attribute. 

The other area where .NET has seen a lot of optimization is streams using `Span` and `Memory` structures to avoid copying and allocating memory many times. However, a lot of that is already done in general .NET methods under the hood, so you don't have to worry so much about it as a user.

[Writing high-performance fsharp-code](https://www.bartoszsypytkowski.com/writing-high-performance-f-code/) is a good blog post to dig deeper into low-level details.


# Web-server concepts

Your web server should respect the general HTTP status codes: 200 (or 2xx) is a success, and errors/failures should return a non-200 code.
For security reasons, the production server should not expose detailed error responses but just a high-level failure reason. The test environment should respond the detailed errors. The technical APIs should be stateless: Building chatty APIs is an anti-pattern because it multiplies the network lag effect.
If your web server does a WebApi and responds in XML/JSON, it would be most straightforward for the consumers to respect the contract and send the errors in that document format.

The typical HTTP verbs are GET/PUT/POST/DELETE. A GET request should not modify the state of your server because proxies can cache them. An internet proxy may respond a cached GET request response to the user without ever reaching your server.

Broken access control is a typical vulnerability: Forgetting to mark an API method to require proper authentication.

Sometimes, the requests are large. Responses can be compressed if both the client and server support the same compression. Some components/configuration is typically needed. (The same applies to sending as a stream.)
The three most typical compression methods are Brotli (fast), deflate (fast), and Zip (not so fast but better support, often as backup).
Compression should be used only for resources larger than a specific size (e.g., 10kb).

Besides client-side caching (which e.g. in a browser you can bypass by pressing Shift+F5), the server-side can also cache.

### This is eTag-caching:

- When the server reads the content before compression, it calculates a hash code over it.
- The hash code is sent as an ETag response header to the client with the response
- The next time the client asks for the same resource, it sends an If-None-Match header with the same value in the request.
- After the server reads the content before the compression, it calculates a hash code over it. If it matches the If-None-Match of the request, the server can skip the (compression + sending). Instead, it'll send empty HTTP status code 304 to the client, which means "use what you have; it's not modified since".

### Basic Search engine optimization (SEO) for an HTML web server:

Some companies claim to do SEO, often running only automatic tools ([GtMetrix](https://gtmetrix.com/), YSlow, browser dev tools, Edge Web Developer Checklist, Lighthouse, Pa11y), mindlessly claiming reported issues. You can use GtMetrix yourself; you don't need a consultant company for that. Some of these "findings" are subjective: It's better to load one cached resource 5 times, than 5 times a few bits of slightly differently optimized resource. Optimal client-side caching time depends on the frequency of your modifications to that resource. The most sensitive data should use the `Cache-control: no-store` header.

Some generic SEO:

- Minify text files like JS+HTML+CSS, and use compression for those MIME types.
- Run w3c html-validator through to see that you send generally valid-looking HTML.
- Check the image file sizes. Prefer vector (SVG) graphics on non-photographic images like clipart.
- For other images, try to use web portable image formats like WEBP.
- Put your static resources into a content delivery network (CDN) server and clean that on the CI/CD deployment pipeline.
- Load your JS async or deferred if possible.
- Can you trust 3rd party hosted JS? What if the resource is down or compromised? Should you instead host it on your server?

### Owin Pipeline

Owin-pipeline is a concept used in modern web servers (like Kestrel/AspNetCore).
The idea is to plug together multiple asynchronous components as a pipeline.

The order of these computations does matter!

A component signature is `Func<Context, Func<Task<_>>, Task<_>>` (so: `context -> ( _ -> T) -> T` ) where context holds the request and response (state), and another parameter is "the next component in line".

You could argue that selecting this state-monad style context, where request and response are hidden from the context, is the wrong abstraction, and index-monad would be better.
However, modelling whatever structure is just a bureaucratical boilerplate, considering limited value to functionality as per se.

A component works like this:

1. Previous component calls this component.
2. (Placeholder to process this functionality)
3. Call the next component
4. (Placeholder to process this functionality)
5. Return

Whether the component executes its functionality in place 2 or place 4 depends on the component (sadly, it is not clearly visible to the outside).
For example: 
Let's say the next component in line is a static file server that reads a file from the server disk surface.
Then, an authentication component trying to protect a resource from unwanted access should be executed in step 2.
However, a component that compresses the response should execute its functionality as late as possible before sending it, so step 4.

So, if you register a pipeline of "A B C D", then the call pipeline will be:
`A -> B -> C -> D -> C -> B -> A`

For this reason, finding the correct order for the components is essential but unclear.


# Localization

Often, localization is premature optimization because the main work is not software: e.g. Do you have a separate customer service phone line per language?

Localization can be dynamic (on the fly) or static (pre-compile different versions).
Dynamic localization will affect internet application performance and SEO because not all robots can execute dynamic code.
Thus, static is a safer bet.

If your technology stack has built-in support for localization (e.g. .resx), it's probably better than building your own (e.g. yaml).

As a rule of thumb:
From a technology stack point of view, localization should be done as late as possible (near the user, preferably in the user interface (UI), not in the server) so that all the information about the user is available.
This way, you can avoid mixing culture-specific information (like number format decimal separators): Is `1,00` + `1.00` = `1,001.00` or `2`?

But that also means the server (and database) should always have all the datetime fields and time-stamps with UTC.
If you don't, you'll struggle to determine which fields are in local format and which are in UTC.
There is no summer/winter time in UTC. So, prefer `UtcNow` over `Now` to get the server time stamp, but note that `UtcNow.Date` does not point to the beginning of the day for a user. 

Also, save the information of the user's time zone with other user data. 
You will need it when you do things like send email/sms to users, which you want to avoid sending in the middle of the night, as users will get annoyed.

Don't trust DateTime.Kind because some libraries will throw "not implemented" exceptions. Instead, consider using DateTimeOffset everywhere (also in the database). If you think you can avoid time zones, for example, Texas has two, and the United Kingdom has overseas territories with nine different ones.

There is an issue with .NET if you try to use date-level without time: DateOnly is not part of the .NET Standard.
You must be careful when moving a date without a time component from client to server because the "00:00:00" time component
may be automatically translated to e.g. "yesterday evening" if automatic time-zone conversions are involved.
(Specifically, when transferring between technologies, like from JavaScript to .NET.)

Other notes:
In lists, sorting is culture-specific. So, for example, Ä (&auml, A with dots) is on some cultures (German) sorted between A and B, and some (Scandinavian) sorted after Z.
Getting it right is very important because people know their alphabet and search for their items from the correct places.


# Functional programming basics

Modern-day software development is more about managing data flows (a bit like in the game [Shapez.io](https://shapez.io/)) rather than writing imperative algorithms with for-loops and if-clauses (like in the game [Shenzhen I/O](https://www.zachtronics.com/shenzhen-io/)).
Functional programming (FP) is a good fit for cloud architectures.

Functions are (by formal definition) one-way relations f(x) = y mapping some input to some output.
(The other way of relation is an inverse function, but this is about functional programming, not relational programming. MiniKanren like tools are another topic.)

In functional programming, functions can be captured as "variables"; for example, a parameter can be another function.
Functions should be mostly side-effect-free and immutable little components that can be composed together (chained after each other).
The function composition can be marked as the "ball" operator in math or as the forward composition operator (`>>` in FSharp) `(g o f)(x) = g(f(x))`.
The basic idea is that you can now capture the "pipeline" without the "object" x, and thus, you can make bigger functions out of a chain of smaller functions (like a "fluent-API").
Function composition enables a dynamic abstraction level, which is better than any of the traditional fixed abstraction levels because it can cover all of them:
procedures, classes, components, services, micro-services, actors, containers, ...
Also, a function(-chain) can be wrapped to be e.g. a micro-service with some call contract (JSON/XML API) if other functions have different technology stacks.

In .NET, the class `Func<a, b>` represents a function with input a and output b. These are marked with an arrow: 
`'a -> 'b`. And function `Func<a, b, c>` represents a function with two inputs and one output. So `'a -> 'b -> 'c`.

Pure function logic can be deducted from the type syntax, e.g. `List<'T> -> ('T -> bool) -> List<'T>` has to be "Where" (a.k.a "filter").
and `List<'T> -> ('T -> 'U) -> List<'U>` has to be "Select" (a.k.a "map").
The list above could be any other context container (a.k.a. "monad"). 
If you read [theoretical papers](https://dl.acm.org/doi/pdf/10.1145/99370.99404), Ɐ means "foreach" and "." implies lambda parameter. If `x` is a parameter, `xs` or `x*` is a list of them.

Functional programming gives you powerful tools (such as "functions as parameters", partial application, and point-free style), and like with any other tool, overusing them ("hammer seeking for nails") could cause some issues (for instance, more complex debugging).

For C# developers, [LINQ](https://github.com/Basware/LINQ-Tutorial/tree/master) is an excellent gateway drug to FP.

### Church encoding

You can actually create numbers with functions. A simple example is: Let's say a number is represented by application times.
E.g. `1 = I(x)` and `2 = I(I(x))` and `3 = I(I(I(x)))` then `2 + 2 = I(I(I(I(x)))) = 4`. That will not work for subtraction and other operations; your function needs more parameters than only the identity function. However, Alonso Church proved with Church numerals that this can be done.
This means the philosophical question "What if 1 + 1 is not 2?" is wrong and irrelevant.
This also means you could transfer numbers (and logic) to domains that have no numbers defined.
All you ever need is a few combinator functions (called s,k,i). A bit like all logic operators can be constructed with `nand`.

### Lambda-calculus and Fixed-point combinator / Y-combinator

With lambda calculus, you have functions without names. So `fun x y -> x + y` doesn't have a name.
But you can also do recursion on domains where there are no function names defined: `fun y g -> y(g(y))` where `y` is a function that causes recursion and `g` is a function that defines a match-stop-condition to recursion.


# Database and transactions, when you need to store data

Some general considerations when using databases:
- Not everyone should use the same user accounts or permissions. There is no reason an internet user should be able to drop tables or modify schemas.
- If you have 10,000 items to process, don't call the database one by one; it will take forever if the network lag is, for example, 0.1 sec/call (a.k.a. N+1 problem).
- But also don't write a single IN-clause with 10,000 parameters. You must chunk your parameter array to be appropriately sized (e.g., 300 per "batch").
- You should be able to query data history (within your company's data retention policy), "How did we end up here?". There are considerable ways to do this, such as using "Temporal tables" or manually avoiding UPDATE and DELETE data and instead having flags like "Created" and nullable "Deleted" as time-stamp columns. This makes it possible to reason about what happened.
- Database usage is IO-bound. Thus, using asynchronous computations (like Tasks) would be best.
- If your database is distributed over multiple untrusted data sources, you may consider a blockchain to exchange computing power for trust. This is not a typical case.
- When selecting id-type: `int` is in the correct order, but `Guid` is more challenging to guess and more manageable to transfer to other databases, but not in order. `RowGuid` is in order but easy to guess. If you ever do Guid ToString, note the random possible case-sensitivity variance (`A15BF7...` vs `a15bf7...`).


### Database speed and indexing 

If your database feels slow, it's probably because your queries are not hitting the indexing or you select too much data.
Check your query's "estimated execution plan".

- Don't join tables unnecessarily to themselves to ensure the columns are available. Instead, select the appropriate columns in the first place.
- Avoid doing select * so that all the columns are indeed added (and indexes can't be used). Select only the meaningful ones.
- Don't use operations like "dateAdd(days" in where-clauses. Operations make indexes unusable. (So e.g. myDateTime >= '2021-01-01' and myDateTime < '2021-01-02' does hit the index, but cast(myDateTime as Date) = '2021-01-01' will not.)
- Avoid doing selects inside selects, so: select id, (select * from Table2 where ...) from Table1 where ...

### Transactions

A transaction is an operation that either succeeds or fails (and reverts a previous state when it failed).
For financial software, you will need transactions. A classic example of moving money from account A to account B should never lead to a failure where either both or none of the accounts have the money.
Sometimes you can try to avoid or split your transactions smaller using event-based architectures, if you are sure the state will still be eventually consistent (at some point in time).
But by default, if you are unsure about transactions, you should use them.

You don't need transactions if you have only read-only operations because there is no state to keep in sync. 
However, if your operation has both read and write operations (or multiple write operations), the full computation should be wrapped inside the same transaction.

Transactions tend to cause a lock over a resource (like database table/rows) so that other parties/processes/users cannot use the resource while the transaction is going on.
That will be an issue if your operation is (possibly) slow (like having a network call) because it will stop all the other parallel use of that resource.
For a modern computer, 1 second is a very long time.
You should avoid spanning transactions over possibly long-running operations, but that needs extra careful error handling: 
If, for example, saving a web service call result fails, the call still happens. So, can it be retried?

There are some settings for transactions in .NET that are a compromise of data consistency vs performance (via locking the database too much):

- TransactionScopeOption: What if you have nested transactions; transactions within a transaction?
     + The default value is "Required", which means that if there is no transaction, create a new one, but if there is already one, then join that one.
       This will ensure data consistency by always having a transaction, but it will also try to avoid deadlocks by not letting transactions wait for each other.

- IsolationLevel: Will the transaction lock the database resources on read and write (Serializable), locking the database from other users while the process is on?
     + The default is ReadCommitted, which means the data is eventually consistent: While the transaction is on, letting others read old data that was valid but not data
       that is written in this transaction.

- TransactionScopeAsyncFlowOption: Since .NET 4.5.1, there has been this option that if a transaction changes the thread (e.g. via async operation),
       then span the transaction to continue in the new thread as well.

If the transactions are set not to commit to nested transactions, or you have operations within the same operation which some use transactions and later ones don't, 
then it's easy to encounter a deadlock where the transaction is waiting for a resource that is waiting for the transaction.

If you try to run multiple queries in parallel within the same connection, the database drivers typically are not very good at this and may throw weird connection pooling issues.
If you run multiple tasks without waiting for the previous one to finish, each task starts immediately, so you may be running parallel tasks. This means the execution order is not defined (and you may encounter race condition issues).

If you do parallel or fire-and-forget tasks, be explicit that they are on purpose (not just forgotten await or other coding accident), i.e., with the corresponding method naming.

# Message queue architectures

The need for message-driven architectures comes from scalability requirements.
If you have too many concurrent users, you cannot save everything to the same data storage and expect everyone to use it fluently. (See transactions above.)

The idea is to break the function composition pipeline and deploy a function to an independent container with one input and two output queues: the standard output and an error queue.
Then, it can process its messages at its own speed without interfering too much with others, and computing resources can be better deployed where needed. 
Error-queue-events could be re-sent to the input queue or processed by any other error handling.

Production monitoring of these different queues is challenging. "Where are my messages, and why did they end up here?"

There are two main types of messages: Command "execute this call now" (single target) and Event "past tense, this did happen" (publish-subscribe, many listeners).

These "containers" can have a state of their own, which they can try to hide from outside (agent/actor model).

If your domain consists of multiple services, you may end up in a situation where a business domain object looks different in different services:
A "car" may have different properties if you look at it from an insurance point of view versus from a sales-tax point of view. You should not define one huge generic car.
You should find the "bounded context", the essence of a car you need to model in your case.
Typically, it's better to focus on modelling actions (like "driving") rather than subjects (like "car").

What if you see the potentially same message twice (e.g. a timeout and retry)? If your message says "add +3", then the replay is hugely dangerous, as the state corrupts.
If your message says "Change balance from 15 to 18", it's safer but still problematic. The messages should contain some identity (a concept called Idempotency)
so that duplicate events can be identified.

Updates to queue architecture are a challenge of their own: If the message schema (shape) changes, update scripts must be written to migrate the not-yet-processed messages in the queues.
You will probably maintain multiple versions of a service, e.g. deploying to different URLs (`/v2/`). How do you know who is still using the old versions?

You could separate read and write events, write all events initially to an audit-trail / event-log / history, and then parse them asynchronously in the background to
the domain model and validation. This is called event sourcing / CQRS. It applies immutable concepts on an architectural level, but depending on your business needs, it may be an overkill.

If you have concurrent parallel message workflows and have to do synchronizations (Saga-pattern), it will involve some locks on some level.
Therefore, the concurrency scenarios will work fine (IO-bound), but throughput (via parallelism, CPU-bound) will not be optimal.


# MVVM frameworks, reactive programming and MVU.

A framework is a mega-library that takes too much control and context, forcing you to use a single framework instead of the best small libraries/tools to solve your problem.

Typically, developers try to separate schemas/styling (CSS) and user interface layout (e.g. HTML) from the other user-interface parts like input validation, logic code or "model", 
so the style and layout could be made by a designer or some non-developer (as if ever).

Typically, developers try to make generic components to solve all the user interfaces until the next major re-design or branding happens
(through a designer role change or company purchases). The naive, unrealistic idea is that these generic components can be dropped into 
a form to create a new modern user interface.

The side effect of these is different kinds of UI frameworks that use their models (like Model-View-Controller, Model-view-viewmodel (MVVM), etc.).
The current most popular is MVVM, introduced in Silverlight (a precessor of MAUI) and is now used in web frameworks like Angular, React.Js, Knockout, etc.

The common feature is that when you try to build nested components, you have to start to signal the events between components, and the natural way
is usually via "bubbling", like domino block effects, leading to the program state being a colossal mess.

But there is an alternative way: going back to message queues. 
You can think of event feeds as infinite lazy lists that populate as the events happen, like a `List<mouseClicks>` where mouseClick is a type having x and y coordinates.

The concept is called reactive programming (Reactive Extensions, Rx, using `IObservable<T>`):
You can have all events as different lists, combine the feeds and apply list operations (like map/filter) to your event feeds. You can even register subscriptions to the event feeds on the component constructor and then have immutable components. 
Suddenly, you have changed all your complicated concurrency-state-synchronization issues to complicated functional composition issues.
Using new technology is transferring your problems to other problems, but it turns out these other problems might be more interesting
(like sliding window caching, hot- and cold-observables: "If no one listens to my events, do they actually happen?").

React.Js is not based on reactive programming; it's just a naming accident.

There are some undeniable benefits of reactive programming:
You can unit-test the UI logic by manually creating a list of events, publishing it and seeing if subscribers are in the state you expect them to be.
You can create an easy undo history by removing the most recent events.
The criticism of Rx is that you still have to subscribe, and in the subscribe code, you lose your nice functional abstraction.

This base ideology of reactive event-based programming was used to create a programming language called Elm, which used a pattern called Model-View-Update (MVU).
That model has since been used in various UI technologies (Elmish, Fable, Fabulous, ...). To learn more about that, check [the Elmish book](https://zaid-ajaj.github.io/the-elmish-book/#/).


# Usability

User interface usability (UX) is a big topic of its own; use designers' help if possible; they are experts in the field and know the latest fashion.

A few general guidelines:
- Use a maximum of two main fonts in the user interface (with possibly multiple parameters like font weight). 
- Consider colour-blind usage and keyboard shortcuts
- Remember the progress indicator, as some people don't have the latest tech. 
- For a number input, check that the mobile phone gives only the number keyboard. 
- Design choice: Is your user interface full of stuff for daily expert usage (like a Lidl sales brochure) or a single white box on a white screen for a random consumer (like an Apple product)?
- Computers only do what they are asked to do. But people make errors. Is there "Undo"? How much data is lost if a computer runs out of battery?
- People always prefer initially what is the old and familiar.
- For websites, the SEO tools (listed earlier) do check some basic accessibility tests to ensure you are at least WCAG 2.2 AA compliant

# Infrastructure as code

Infrastructure-as-code is an idea to script the entire environment deployment and configuration scripts as code for cloud environment scenarios.
The benefits are clear: Backups and version control. You can re-deploy changes only.

Like all backups, the infrastructure script has to be tested. Without testing, you'll have a false sense of security.
It's not enough to download the ARMs from Azure to source control and expect to be able to restore them later. You can't, meaning you don't have backups.

The basic templates are thousands of lines of unmaintainable JSON. But there are a few tools (e.g. [Farmer](https://compositionalit.github.io/farmer/) or Pulumni) that take the approach of having good pre-configurations
and only list changes, so even an extensive enterprise infrastructure is only a few hundred lines of code.

As usual, compilation is better than configuration: You are lucky if you have a compiler verifying your scripts.
Compilation is also better than generation: The compiler is built so that you can recompile, and there is no need to mess with lower abstraction levels.

The scripts should be compiled/created by a continuous integration pipeline. They should be in a state that you, the creator, are not the single point of failure: The parameters should be generally available, and the sensitive ones documented how to access.

Consider the infrastructure resource naming well in the first place: you will have a challenging second change when your business extends to a new location.

For straightforward maintenance, focus on forward migration scripts only, not rollbacks.

# Continuous integration (CI) / Continuous delivery (CD)

What the CI/CD pipeline should do:

CI:
1. Wake up on version control changes, get the proper version
2. Build the version control branch, the solution, and tests. 
3. Run code-analysis rules.
4. Build infrastructure-as-code script.
5. Generate documentation (e.g. API references).
6. Run unit tests and measure the result (and code coverage, hopefully somewhere around 15%-35%)
7. Build and run integration tests
8. Ensure configurations are correct (e.g. binding redirects)
9. Publish built artifacts. Create an installation package.
10. Deploy to test-environment (or test-app-store)
11. Run smoke tests (or acceptance tests) against the deployed environment
12. Run security / penetration tests. (Running these should not create an illusion of safety)
13. Purge test-CDN cache

CD:

14. Deploy to production (or actual app store)
15. Purge production CDN cache
16. Add a tag to version control about the release
17. If deploying a website, check it's live ("smoke-test").

That's it. If it's not doing that, then there is room for improvement.
How often do you want to release, do you want a code-freeze-testing period, and from which kind of Git branching-strategy, is a separate conversation. 
Don't run production deployment as the last thing on Friday evening. There is not enough customer service and support working on weekends in case of errors.

# .NET Framework 4.x maintenance path

.NET Framework 4.x will be maintained about forever (when writing this, as big banks are using it). However, if you want to enable taking advantage of the latest performance improvements,
you may want to make your old code able to run on .NET 10/11/whatever. The easiest way to do the conversion:
Create a new library project built on `<TargetFrameworks>netstandard2.0;netstandard2.1</TargetFrameworks>` and move your existing code files there.
Then, ensure you have all the third-party reference components compatible with those. Delete the `obj` and `bin` folders if you need to rebuild and restore packages.
If you have issues with your own code, use for example conditional compilation. As both the old .NET Framework and new .NET are both compatible with .NET Standard,
you should now be able to reference your library under either of the .NET versions.
After that, if you want to use more recent .NET versions, add them to the target frameworks.

# Security

Here is some guidance on how to hack your system (or try to protect it from hacking).
An excellent place to keep up with security is to check the latest OWASP top 10 attacks.

Have you considered what is the attack surface of your software?
What are the gains of compromising something?
Attackers are only sometimes after money or access to your systems; they can also try to access users' information.
Thus, sensitive information (like social security numbers and US bank account numbers) should be masked and not easily visible.

Some general ways to attack:
- Run vulnerability scanners or port scanners. See DNS subdomains.
- Block communication totally
- Record and replay old signal (with possible parametrization)
- Directly manipulate the data in communication (e.g. a client-side code).
- Compromised 3rd party dependencies

Let's start with an easy one, a login guess attack: Mr X has bought from the dark web a leaked MySpace email+password database of 360M emails. 
Now he records a Selenium web recording of your login sequence,
parametrizes the email and password fields, and replays all the traffic through. People might use the same passwords on many services. How do you block it?
He may use, for example, 250 computers to distribute the traffic sources (with a botnet or Azure load-testing tool or whatever).
Locking accounts after three failures could convert to a Denial of Service attack where everyone is locked. What you can do: 
- Slowdown relogin-attempts by increasing timeout. Save login trials to a store so you'll know when you're under an attack.
- Support multi-factor authentication (MFA) and consider demanding that from everyone.
- Show the user their last login IPs. If they are logged in from a new location, consider emailing them.
- Do you have copy&paste allowed in your input fields?
External fraud products are available, such as checking the customer's typing speed and whether their mouse moves often outside a browser window (to copy data from Excel).

The minimum password should be... But what if someone has a password of 100 megabytes in size? Have you restricted all your input fields? How about direct WebApi calls?
If someone's email is `<script>document.location.href=badsite.com</script>@gmail.com`, will the customer service agent searching for this guy go to a bad site?
Have you adequately sanitized all input (HTML and URL encoding), restricted it somehow, or installed a web application firewall? Have you tested whether you can hack your protection?
Are other users anyhow visible to each other? 

How much input validation should be done? Input is typically one place, and output (data usage) may be more than 10. 
If you validate the input level, you refrain from handling weird conversions when you use the data.
The quality of the data you take in will affect the quality of the data you get out of your system.

Next, Mr. X will move to the "Reset Password" feature...
- Install Captcha "I'm not a robot" service on unauthenticated service endpoints (also to login above).
- You should never save user passwords to your system. Instead, you should save only calculate a hash-code (e.g. SHA512) over the password and save the hash-code to the database. Next time they login, you calculate the hash code over the given password and match it to the one you saved. This will make it impossible to steal passwords (but also make password changes more interesting).

What happens if you open the same instance (e.g. browser "open link in a new tab") twice? Can you submit the data twice? Can you advance to another tab and then save old data from another?
If the system has bread-crumb-path links, can you open a previous page from the link, save the data, and then use the browser back button to jump forward with skipping validation? (Example: "Step 1: Select sum -> Step 2: Calculate price -> Step 3: Summary and send", fill the application with a sum $10 to "Step 3", press the bread-crumb link to "Step 1", change the sum to $100 and save, then press the browser back to jump back to the summary, and send the data without re-calculation on step 2.)

SQL-injection attack is [classic, Bobby tables](https://xkcd.com/327/), but it can also be easily blogged by not creating manual SQL clauses by string-parsing but instead via parameterized SQL.
Parametrized SQL is also better because it uses a stored procedure cache, and the query executes faster. SQL injection can also be done by other means, for example, via cookie poisoning instead of direct form fields.

Meanwhile, Mr X has set up his own web server where he hosts a proper-looking web page, but he has coded a unique feature to his "I accept cookies" button: Submit makes a post-request to your website. So if a user (a victim visiting his website) has a session open on your website, Mr X is now able to make custom calls to your site as the victim. (Or Mr. X could also host your page in an iframe and override your "I accept cookies" button with his absolute position button.) This attack is called Cross-Site Request Forgery (CSRF) and can be mitigated with CSRF tokens.

If your system deals with files, that's another possible attack vector: Can the access be compromised? Are the files backed up? Can the backup be compromised?
Can the file contain a virus? What if a file is a gigabyte "by accident"?

Can you hide a secret in the code? On the client side, you cannot, as people can always "view source" or reverse engineer things.
(e.g. Chrome: F12 developer tools, press the "Sources" tab, select a file, and click the {} button to format it nicely).
The server side may also be unsafe, but it's not leaking as fast. Install the tool [IlSpy](https://github.com/icsharpcode/ILSpy/releases) to investigate what you can get out of the .NET dlls.
You can also save the reverse-engineered source code as csproj if needed. You'll lose local variable names, but for example, all the constants and methods are there.

Some limited levels of security testing can be automated via vulnerability scanners (like Acunetix, Nessos or [Zap](https://www.zaproxy.org/) or [wfuzz](https://github.com/xmendez/wfuzz)), so it's a good habit to run one before publishing new software to the Internet, and then regularly at least a few times a year. Some companies do that as part of the CI/CD pipeline.


### Safe way to transfer data via a non-trusted party (client side)

Sometimes, you have to transfer data over non-trusted party channels. That is never safe. But the best bet is:
- Add a time-stamp to the data.
- Calculate a hash code over data and some secret value ("salt") e.g. Guid or whatever. Include the hash code in the "headers" when sending it to the client.
- When the data is gotten from an untrusted party, recalculate the hash code over it and check that it matches the hash code in the headers.
- ...But also take the time stamp from the data and check if it's still within valid time tolerance. Without the time stamp, the attacker can brute-force the correct hash code over a long time period.

### Basic web-server settings

- HTTP should not be allowed on anything (except a generic social media icon) and should always redirect to HTTPS.
- CORS policies should state that you can't explicitly refer to scripts from other websites. Only add CORS locations you trust; otherwise, you make CSRF attacks easier.
- Error pages should only display detailed errors in test-environments/debug-mode. Refrain from giving details in production; it would help hackers to understand your technology stack and focus their attacks.
- If you use cookies (like ASPNET session cookie), make sure proper flags are set (HttpOnly, Secure)
- What happens if you press the F5 key for a long time on your web page? Will the server handle the single-user-generated load without problems? Or replays a slow operation in many browser tabs?
- Note: If you manually adjust your computer clock via date or time command, some of the web server settings might be broken until you restart
- By default, your page can be loaded by a hostile party in IFRAME. Are you checking whether your code is running in a non-same-site frame?
- The UserAgent string can be interesting informationally, but it's untrustworthy. Users can write whatever they like there. Also, IPs can be renewed in a script, and the network card MAC address can be set manually.
- Be extra careful when validating redirects. Users can try redirecting to restricted paths with "../config.yaml" but also make cross-site scripting (XSS) by redirecting to other websites. For example, if your trusted `originalsite.com` has a parameter RedirectUrl, simple "contains" and "no http" is not enough because a user might do 
`?RedirectUrl=https://badsite.com/originalsite.com/bad.html` or `?RedirectUrl=https://originalsite.com.badsite.com` or `?RedirectUrl=http%3A%2F%%2Fbadsite.com` and so on. Instead of trying to black-list all possible bad requests, safelist only the allowed locations. The typical XSS: hacked trusted URL redirects it to the hacker's site, then emails users about them urgently needing to visit the trusted link.

The attacker gains the best results by combining multiple different types of attacks.

# IT Contracts with 3rd parties

Contracts are not directly the developer's responsibility, but some general knowledge because software purchases often go wrong.
There are some things non-legal people should be aware of when legal (IT-related) contracts with 3rd parties are being signed:

- Fees: Is VAT included? Have estimated volumes appropriately calculated?

- Contract Term & Exiting a Contract: What are the rights to terminate? Notice period with auto-renewal?

- Mistakes: Are penalties agreed upon if the supplier makes late deliveries? Is the final payment only if acceptance tests are passed? Service level (SLA) allowed downtime for critical services? How trouble tickets are handled?

- Specifications: Is it clear what is being bought? Have requirements been gathered and requirement specifications completed? Specs have to be central. Refrain from trusting URLs or vague specs in the schedule. What is the definition of "done"? Is acceptance defined? Assumptions and dependencies to other systems have to be listed at the contractual level.

- Indemnities and Liability: When things go wrong, what is the limit of liabilities? What are the governing laws and disputes?

- Intellectual Property: It has to cover the countries needed. Who has access to the source code? Is there joint development? Are the used open-source licences checked? Who owns the data if the contract is terminated, and how can it be transferred?

- Data Protection and Security: Are you giving out customer data? If yes, the contract has to define GDPR compliance and the customer's consent to use the data for that purpose. What is the supplier country? Are the data transfers permitted by, e.g., GDPR? How well is the third party protecting data?

- A legal department needs information for their review: Why this contract? Milestone dates? Objectives of the project? Assumptions and dependencies? What is a must, and what is a nice-to-have? History with the supplier. Deadline date for signing.

# There are still many topics uncovered

Software engineering is typically more about people than code.
Daniel Pink has listed [what motivates developers](https://www.tutor2u.net/business/reference/motivation-pink-three-elements-of-intrinsic-motivation).
And [zen-lab](https://www.cl.cam.ac.uk/~jac22/zen-lab.txt) has an article on creating a suitable environment for talented people.

Have you ever been given software to maintain, where the original developer just published it and then left for a holiday?
When you are that original developer, think about the whole maintenance experience of the system you made: is it evident for the next guy and enough documented? 

Honest collaboration produces the best results. Share knowledge, ask questions, seek answers, and even challenge others if they are wrong. 

However, that's it for now. Happy development!