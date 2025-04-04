namespace OrderProcessing
open System
open System.IO

type Order = {
    Id: int
    Type: string
    Amount: float
    Flag: bool
    Status: string
    Priority: string
}

type APIResponse = {
    Status: string
    Data: obj
}

exception APIException of string
exception DatabaseException of string

type IDatabaseService =
    abstract member GetOrdersByUser: int -> Order list
    abstract member UpdateOrderStatus: int -> string -> string -> bool

type IAPIClient =
    abstract member CallAPI: int -> APIResponse

module OrderProcessingService =
    let processOrders (dbService: IDatabaseService) (apiClient: IAPIClient) (userId: int) =
        try
            let orders = dbService.GetOrdersByUser userId
            
            if List.isEmpty orders then
                false
            else
                orders |> List.iter (fun order ->
                    let updatedOrder =
                        match order.Type with
                        | "A" ->
                            let csvFile = sprintf "orders_type_A_%d_%d.csv" userId (DateTime.Now.Ticks / 10000000L)
                            try
                                use fileHandle = new StreamWriter(csvFile)
                                fileHandle.WriteLine("ID,Type,Amount,Flag,Status,Priority")
                                fileHandle.WriteLine(sprintf "%d,%s,%.2f,%b,%s,%s" 
                                    order.Id order.Type order.Amount order.Flag order.Status order.Priority)
                                if order.Amount > 150.0 then
                                    fileHandle.WriteLine(",,,,Note,High value order")
                                { order with Status = "exported" }
                            with
                            | :? IOException -> { order with Status = "export_failed" }
                        | "B" ->
                            try
                                let apiResponse = apiClient.CallAPI order.Id
                                match apiResponse.Status with
                                | "success" ->
                                    let data = apiResponse.Data :?> int
                                    if data >= 50 && order.Amount < 100.0 then
                                        { order with Status = "processed" }
                                    elif data < 50 || order.Flag then
                                        { order with Status = "pending" }
                                    else
                                        { order with Status = "error" }
                                | _ -> { order with Status = "api_error" }
                            with
                            | :? APIException -> { order with Status = "api_failure" }
                        | "C" ->
                            if order.Flag then
                                { order with Status = "completed" }
                            else
                                { order with Status = "in_progress" }
                        | _ -> { order with Status = "unknown_type" }

                    let finalOrder =
                        if updatedOrder.Amount > 200.0 then
                            { updatedOrder with Priority = "high" }
                        else
                            { updatedOrder with Priority = "low" }

                    try
                        dbService.UpdateOrderStatus finalOrder.Id finalOrder.Status finalOrder.Priority |> ignore
                    with
                    | :? DatabaseException -> 
                        { finalOrder with Status = "db_error" } |> ignore
                )
                true
        with
        | _ -> false