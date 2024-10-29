﻿open System
open System.Threading
open FSharp.Control
open System.Net.Http
open System.Text
open System.Text.Json
open Domain
open System.Diagnostics
open System.IO
open System.Threading.Tasks

let mutable lastLineCount = 0

module UI =
    let spinner () =
        let mutable running = true

        while running do
            for c in "|/-\\" do
                Console.Write(sprintf "\r%c" c)
                Thread.Sleep(100)

            if Console.KeyAvailable && Console.ReadKey(true).Key = ConsoleKey.Escape then
                running <- false

    let saveStartingPoint () = printf "\u001b[s"

    let printInPlaceSmooth (text: string) =
        async {
            let startInfo = ProcessStartInfo()
            startInfo.FileName <- "glow"
            startInfo.Arguments <- "-" // Read from stdin
            startInfo.RedirectStandardInput <- true
            startInfo.RedirectStandardOutput <- true
            startInfo.UseShellExecute <- false
            use _process = new Process()
            _process.StartInfo <- startInfo
            _process.Start() |> ignore
            do! _process.StandardInput.WriteLineAsync(text) |> Async.AwaitTask
            _process.StandardInput.Close()
            let! output = _process.StandardOutput.ReadToEndAsync() |> Async.AwaitTask
            do! _process.WaitForExitAsync() |> Async.AwaitTask

            // Move cursor up by the number of lines we printed last time
            if lastLineCount > 0 then
                printf "\u001b[%dA" lastLineCount

            // Split output into lines
            let lines = output.Split('\n')
            lastLineCount <- lines.Length

            // Print each line, clearing to the end of line for each
            for line in lines do
                printf "%s\u001b[K\n" line

            // Move cursor up to the end of our output
            printf "\u001b[%dA" lines.Length
        }

    let printInPlace (text: string) =
        async {
            //move cursor back to starting point
            printf "\u001b[u"

            // //clear everything
            printf "\u001b[0J"

            let startInfo = ProcessStartInfo()
            startInfo.FileName <- "glow"
            startInfo.Arguments <- "-" // Read from stdin
            startInfo.RedirectStandardInput <- true
            startInfo.UseShellExecute <- false

            use _process = new Process()
            _process.StartInfo <- startInfo
            _process.Start() |> ignore

            do! _process.StandardInput.WriteLineAsync(text) |> Async.AwaitTask
            _process.StandardInput.Close()

            do! _process.WaitForExitAsync() |> Async.AwaitTask

        }

    let display (state: State) =

        let printBold (text: string) = printf "\u001b[1m%s\u001b[0m" text

        match state.Message with
        | You _ ->
            printBold ">>>"
            printf " "
        | Jarvis _ -> ()
        | Quit -> ()

        state

let printBold (text: string) = printf "\u001b[1m%s\u001b[0m" text

let countWrappedLines (text: string) =
    let terminalWidth = Console.WindowWidth

    let folder (currentCol, totalLines) c =
        match c with
        | '\n' -> (0, totalLines + 1)
        | _ when currentCol + 1 >= terminalWidth -> (1, totalLines + 1)
        | _ -> (currentCol + 1, totalLines)

    text |> Seq.fold folder (0, 0) |> snd

let clearUpToLine n =
    printf "\u001b[%dA" n
    printf "\u001b[G"
    printf "\u001b[0J"

let withNewChat (msg: Message) (convo: Conversation) =
    match msg with
    | Quit -> convo
    | You msg -> [ yield! convo; You msg ]
    | Jarvis msg -> [ yield! convo; Jarvis msg ]

let askJarvis prompt state : string =
    // Ollama.createPayload "mixtral:8x7b" state.Conversation
    Ollama.createPayload "codellama" state.Conversation
    |> Ollama.makeRequest
    |> (fun stream ->
        async {
            let mutable res = ""

            try
                do!
                    stream
                    |> AsyncSeq.iterAsync (fun content ->
                        async {
                            res <- res + content
                            printf $"{content}"
                        })

            with
            | :? OperationCanceledException -> printfn "Streaming was canceled."
            | ex -> printfn "An error occurred during streaming: %s" ex.Message

            clearUpToLine (countWrappedLines res)

            let startInfo = ProcessStartInfo()
            startInfo.FileName <- "glow"
            startInfo.Arguments <- "-" // Read from stdin
            startInfo.RedirectStandardInput <- true
            startInfo.UseShellExecute <- false

            use _process = new Process()
            _process.StartInfo <- startInfo
            _process.Start() |> ignore

            do! _process.StandardInput.WriteLineAsync(res) |> Async.AwaitTask
            _process.StandardInput.Close()

            do! _process.WaitForExitAsync() |> Async.AwaitTask

            return res
        })
    |> Async.RunSynchronously

let withNewestPrompt state = List.last state.Conversation

let rec chat (state: State) =
    match state.Message with
    | You prompt ->
        state |> UI.display |> ignore //print initial UI

        let input = System.Console.ReadLine() //ask for user input -> msg

        let newState =
            match input with
            | "exit"
            | "quit" -> { state with Message = Quit }
            | str when str.Length <> 0 ->
                { state with
                    Message = You(prompt + "\n" + str) }
            | "" ->
                { state with
                    Message = Jarvis ""
                    Conversation = withNewChat (You prompt) state.Conversation } //end
            | _ -> { state with Message = You prompt }

        chat newState
    | Jarvis said ->
        state |> UI.display |> ignore //print UI with Jarvis placeholder

        //ask for jarvis input -> ollama rest api call
        let response = state |> askJarvis withNewestPrompt

        chat
            { state with
                Conversation = withNewChat (Jarvis response) state.Conversation
                Message = You "" }
    | Quit ->
        //exit the program
        ()

[<EntryPoint>]
let main args =

    let initially =
        { Message = You ""
          Conversation = List.Empty }

    chat initially

    0 // return an integer exit code
