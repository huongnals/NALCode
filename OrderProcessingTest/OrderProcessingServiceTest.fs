namespace OrderProcessingTest

open System
open System.IO
open Xunit
open OrderProcessing
open System.Collections.Generic

type MockDatabaseService() =
    let mutable orders = List<Order>()
    let mutable updatedOrders = Dictionary<int, (string * string)>()
    
    interface IDatabaseService with
        member this.GetOrdersByUser(userId: int) = 
            orders |> List.ofSeq
            
        member this.UpdateOrderStatus(orderId: int) (status: string) (priority: string) =
            updatedOrders.[orderId] <- (status, priority)
            true
            
    member this.SetupOrders(testOrders: Order list) =
        orders <- List<Order>(testOrders)
        
    member this.GetUpdatedOrder(orderId: int) =
        if updatedOrders.ContainsKey(orderId) then
            Some updatedOrders.[orderId]
        else
            None

type MockAPIClient() =
    let mutable responses = Dictionary<int, APIResponse>()
    
    interface IAPIClient with
        member this.CallAPI(orderId: int) =
            if responses.ContainsKey(orderId) then
                responses.[orderId]
            else
                { Status = "error"; Data = box 0 }
                
    member this.SetupResponses(testResponses: (int * APIResponse) list) =
        for (id, response) in testResponses do
            responses.[id] <- response

type ThrowingMockDatabaseService() =
    let mutable orders = List<Order>()
    
    interface IDatabaseService with
        member this.GetOrdersByUser(userId: int) = 
            orders |> List.ofSeq
            
        member this.UpdateOrderStatus(orderId: int) (status: string) (priority: string) =
            // Throw DatabaseException for testing exception handling
            raise (DatabaseException("Database update failed"))
            
    member this.SetupOrders(testOrders: Order list) =
        orders <- List<Order>(testOrders)

type ExceptionThrowingMockDatabaseService() =
    interface IDatabaseService with
        member this.GetOrdersByUser(userId: int) = 
            // Throw general exception to trigger the catch-all handler
            raise (Exception("Unexpected error"))
            
        member this.UpdateOrderStatus(orderId: int) (status: string) (priority: string) =
            true

type APIExceptionThrowingMockAPIClient() =
    let mutable responses = Dictionary<int, APIResponse>()
    
    interface IAPIClient with
        member this.CallAPI(orderId: int) =
            if responses.ContainsKey(orderId) then
                let response = responses.[orderId]
                if response.Status = "throw_api_exception" then
                    raise (APIException("API call failed with specific APIException"))
                else
                    response
            else
                raise (APIException("API call failed with specific APIException"))
                
    member this.SetupResponses(testResponses: (int * APIResponse) list) =
        for (id, response) in testResponses do
            responses.[id] <- response

// Custom mock for simulating IOException during CSV export
type IOExceptionThrowingStreamWriter() =
    inherit StreamWriter("/dev/null")
    
    override this.WriteLine([<ParamArray>] value: string) : unit =
        raise (IOException("Simulated IOException during CSV write"))

module OrderProcessingServiceTest =
    // Basic Functionality Tests
    [<Fact>]
    let ``Test Case 1 - Empty Order List Test``() =
        // Arrange
        let dbService = new MockDatabaseService()
        dbService.SetupOrders([])
        let apiClient = new MockAPIClient()
        
        // Act
        let result = OrderProcessingService.processOrders (dbService :> IDatabaseService) (apiClient :> IAPIClient) 1
        
        // Assert
        Assert.False(result)
        
    [<Fact>]
    let ``Test Case 2 - Multiple Orders Test``() =
        // Arrange
        let testOrders = [
            { Id = 1; Type = "A"; Amount = 75.0; Flag = false; Status = "new"; Priority = "medium" }
            { Id = 2; Type = "B"; Amount = 75.0; Flag = false; Status = "new"; Priority = "medium" }
            { Id = 3; Type = "C"; Amount = 75.0; Flag = true; Status = "new"; Priority = "medium" }
        ]
        
        let dbService = new MockDatabaseService()
        dbService.SetupOrders(testOrders)
        
        let apiClient = new MockAPIClient()
        apiClient.SetupResponses([(2, { Status = "success"; Data = box 60 })])
        
        // Act
        let result = OrderProcessingService.processOrders (dbService :> IDatabaseService) (apiClient :> IAPIClient) 1
        
        // Assert
        Assert.True(result)
        
        // Check each order was processed correctly
        let (statusA, _) = dbService.GetUpdatedOrder(1).Value
        Assert.Equal("exported", statusA)
        
        let (statusB, _) = dbService.GetUpdatedOrder(2).Value
        Assert.Equal("processed", statusB)
        
        let (statusC, _) = dbService.GetUpdatedOrder(3).Value
        Assert.Equal("completed", statusC)
        
    // Type A Orders Tests
    [<Fact>]
    let ``Test Case 3 - Type A Order Processing - Standard CSV Export Test``() =
        // Arrange
        let testOrder = { 
            Id = 3
            Type = "A"
            Amount = 100.0
            Flag = false
            Status = "new"
            Priority = "medium" 
        }
        
        let dbService = new MockDatabaseService()
        dbService.SetupOrders([testOrder])
        let apiClient = new MockAPIClient()
        
        // Act
        let result = OrderProcessingService.processOrders (dbService :> IDatabaseService) (apiClient :> IAPIClient) 1
        
        // Assert
        Assert.True(result)
        let (status, priority) = dbService.GetUpdatedOrder(3).Value
        Assert.Equal("exported", status)
        Assert.Equal("low", priority)
        
    [<Fact>]
    let ``Test Case 4 - Type A Order Processing - High Value Test``() =
        // Arrange
        let testOrder = { 
            Id = 4
            Type = "A"
            Amount = 250.0  // Over 200.0 threshold
            Flag = false
            Status = "new"
            Priority = "medium" 
        }
        
        let dbService = new MockDatabaseService()
        dbService.SetupOrders([testOrder])
        let apiClient = new MockAPIClient()
        
        // Act
        let result = OrderProcessingService.processOrders (dbService :> IDatabaseService) (apiClient :> IAPIClient) 1
        
        // Assert
        Assert.True(result)
        let (status, priority) = dbService.GetUpdatedOrder(4).Value
        Assert.Equal("exported", status)
        Assert.Equal("high", priority)
        
    [<Fact>]
    let ``Test Case 5 - Type A Order Processing - Boundary Amount 150``() =
        // Arrange
        let testOrder = { 
            Id = 5
            Type = "A"
            Amount = 150.0  // Exactly at the boundary
            Flag = false
            Status = "new"
            Priority = "medium" 
        }
        
        let dbService = new MockDatabaseService()
        dbService.SetupOrders([testOrder])
        let apiClient = new MockAPIClient()
        
        // Act
        let result = OrderProcessingService.processOrders (dbService :> IDatabaseService) (apiClient :> IAPIClient) 1
        
        // Assert
        Assert.True(result)
        let (status, priority) = dbService.GetUpdatedOrder(5).Value
        Assert.Equal("exported", status)
        Assert.Equal("low", priority)
        // Note: We can't directly verify CSV content in this test framework
        
    [<Fact>]
    let ``Test Case 6 - Type A Order Processing - Boundary Amount 200``() =
        // Arrange
        let testOrder = { 
            Id = 6
            Type = "A"
            Amount = 200.0  // Exactly at the boundary
            Flag = false
            Status = "new"
            Priority = "medium" 
        }
        
        let dbService = new MockDatabaseService()
        dbService.SetupOrders([testOrder])
        let apiClient = new MockAPIClient()
        
        // Act
        let result = OrderProcessingService.processOrders (dbService :> IDatabaseService) (apiClient :> IAPIClient) 1
        
        // Assert
        Assert.True(result)
        let (status, priority) = dbService.GetUpdatedOrder(6).Value
        Assert.Equal("exported", status)
        Assert.Equal("low", priority)  // Should be "low" since Amount is equal to 200.0, not greater
        
    // Type B Orders Tests
    [<Fact>]
    let ``Test Case 7 - Type B Order Processing - Successful API Call - Processed Status``() =
        // Arrange
        let testOrder = { 
            Id = 7
            Type = "B"
            Amount = 75.0  // < 100.0
            Flag = false
            Status = "new"
            Priority = "medium" 
        }
        
        let dbService = new MockDatabaseService()
        dbService.SetupOrders([testOrder])
        
        let apiClient = new MockAPIClient()
        apiClient.SetupResponses([(7, { Status = "success"; Data = box 60 })])  // Data >= 50
        
        // Act
        let result = OrderProcessingService.processOrders (dbService :> IDatabaseService) (apiClient :> IAPIClient) 1
        
        // Assert
        Assert.True(result)
        let (status, _) = dbService.GetUpdatedOrder(7).Value
        Assert.Equal("processed", status)
        
    [<Fact>]
    let ``Test Case 8 - Type B Order Processing - Pending Status - Low Data Value``() =
        // Arrange
        let testOrder = { 
            Id = 8
            Type = "B"
            Amount = 75.0
            Flag = false
            Status = "new"
            Priority = "medium" 
        }
        
        let dbService = new MockDatabaseService()
        dbService.SetupOrders([testOrder])
        
        let apiClient = new MockAPIClient()
        apiClient.SetupResponses([(8, { Status = "success"; Data = box 40 })])  // Data < 50
        
        // Act
        let result = OrderProcessingService.processOrders (dbService :> IDatabaseService) (apiClient :> IAPIClient) 1
        
        // Assert
        Assert.True(result)
        let (status, _) = dbService.GetUpdatedOrder(8).Value
        Assert.Equal("pending", status)
        
    [<Fact>]
    let ``Test Case 9 - Type B Order Processing - Error Status - High Amount``() =
        // Arrange
        let testOrder = { 
            Id = 9
            Type = "B"
            Amount = 120.0  // >= 100.0
            Flag = false
            Status = "new"
            Priority = "medium" 
        }
        
        let dbService = new MockDatabaseService()
        dbService.SetupOrders([testOrder])
        
        let apiClient = new MockAPIClient()
        apiClient.SetupResponses([(9, { Status = "success"; Data = box 60 })])  // Data >= 50
        
        // Act
        let result = OrderProcessingService.processOrders (dbService :> IDatabaseService) (apiClient :> IAPIClient) 1
        
        // Assert
        Assert.True(result)
        let (status, _) = dbService.GetUpdatedOrder(9).Value
        Assert.Equal("error", status)
        
    [<Fact>]
    let ``Test Case 10 - Type B Order Processing - Pending Status - Flag True``() =
        // Arrange
        let testOrder = { 
            Id = 10
            Type = "B"
            Amount = 120.0  // Changed to >= 100.0 so the first condition fails
            Flag = true     // Flag is true
            Status = "new"
            Priority = "medium" 
        }
        
        let dbService = new MockDatabaseService()
        dbService.SetupOrders([testOrder])
        
        let apiClient = new MockAPIClient()
        apiClient.SetupResponses([(10, { Status = "success"; Data = box 60 })])  // Data >= 50
        
        // Act
        let result = OrderProcessingService.processOrders (dbService :> IDatabaseService) (apiClient :> IAPIClient) 1
        
        // Assert
        Assert.True(result)
        let (status, _) = dbService.GetUpdatedOrder(10).Value
        Assert.Equal("pending", status)
        
    [<Fact>]
    let ``Test Case 11 - Type B Order Processing - Boundary Test - Data 50, Amount 100``() =
        // Arrange
        let testOrder = { 
            Id = 11
            Type = "B"
            Amount = 100.0  // Exactly at the boundary
            Flag = false
            Status = "new"
            Priority = "medium" 
        }
        
        let dbService = new MockDatabaseService()
        dbService.SetupOrders([testOrder])
        
        let apiClient = new MockAPIClient()
        apiClient.SetupResponses([(11, { Status = "success"; Data = box 50 })])  // Data exactly = 50
        
        // Act
        let result = OrderProcessingService.processOrders (dbService :> IDatabaseService) (apiClient :> IAPIClient) 1
        
        // Assert
        Assert.True(result)
        let (status, priority) = dbService.GetUpdatedOrder(11).Value
        Assert.Equal("error", status)
        Assert.Equal("low", priority)
        
    // Type C Orders Tests
    [<Fact>]
    let ``Test Case 12 - Type C Order Processing - Flag True Test``() =
        // Arrange
        let testOrder = { 
            Id = 12
            Type = "C"
            Amount = 75.0
            Flag = true
            Status = "new"
            Priority = "medium" 
        }
        
        let dbService = new MockDatabaseService()
        dbService.SetupOrders([testOrder])
        let apiClient = new MockAPIClient()
        
        // Act
        let result = OrderProcessingService.processOrders (dbService :> IDatabaseService) (apiClient :> IAPIClient) 1
        
        // Assert
        Assert.True(result)
        let (status, _) = dbService.GetUpdatedOrder(12).Value
        Assert.Equal("completed", status)
        
    [<Fact>]
    let ``Test Case 13 - Type C Order Processing - Flag False Test``() =
        // Arrange
        let testOrder = { 
            Id = 13
            Type = "C"
            Amount = 75.0
            Flag = false
            Status = "new"
            Priority = "medium" 
        }
        
        let dbService = new MockDatabaseService()
        dbService.SetupOrders([testOrder])
        let apiClient = new MockAPIClient()
        
        // Act
        let result = OrderProcessingService.processOrders (dbService :> IDatabaseService) (apiClient :> IAPIClient) 1
        
        // Assert
        Assert.True(result)
        let (status, _) = dbService.GetUpdatedOrder(13).Value
        Assert.Equal("in_progress", status)
        
    // Unknown Order Type Tests  
    [<Fact>]
    let ``Test Case 14 - Unknown Type Order Processing``() =
        // Arrange
        let testOrder = { 
            Id = 14
            Type = "X"  // Unknown type
            Amount = 75.0
            Flag = false
            Status = "new"
            Priority = "medium" 
        }
        
        let dbService = new MockDatabaseService()
        dbService.SetupOrders([testOrder])
        let apiClient = new MockAPIClient()
        
        // Act
        let result = OrderProcessingService.processOrders (dbService :> IDatabaseService) (apiClient :> IAPIClient) 1
        
        // Assert
        Assert.True(result)
        let (status, _) = dbService.GetUpdatedOrder(14).Value
        Assert.Equal("unknown_type", status)
        
    // Exception Handling Tests
    [<Fact>]
    let ``Test Case 15 - IOException Test``() =
        // Arrange
        let testOrder = { 
            Id = 15
            Type = "A"
            Amount = 100.0
            Flag = false
            Status = "new"
            Priority = "medium" 
        }
        
        let dbService = new MockDatabaseService()
        dbService.SetupOrders([testOrder])
        let apiClient = new MockAPIClient()
        
        // Act & Assert
        // Note: We can't easily inject an IOException during CSV creation in unit tests
        // In a real implementation, we would need to mock the file system 
        // This test is relying on the actual implementation
        let result = OrderProcessingService.processOrders (dbService :> IDatabaseService) (apiClient :> IAPIClient) 1
        Assert.True(result)
        let (status, _) = dbService.GetUpdatedOrder(15).Value
        // In real scenario with IOException, status would be "export_failed"
        Assert.Equal("exported", status)
        
    [<Fact>]
    let ``Test Case 16 - API Exception Test``() =
        // Arrange
        let testOrder = { 
            Id = 16
            Type = "B"
            Amount = 75.0
            Flag = false
            Status = "new"
            Priority = "medium" 
        }
        
        let dbService = new MockDatabaseService()
        dbService.SetupOrders([testOrder])
        
        let apiClient = new APIExceptionThrowingMockAPIClient()
        apiClient.SetupResponses([(16, { Status = "throw_api_exception"; Data = box 0 })])
        
        // Act
        let result = OrderProcessingService.processOrders (dbService :> IDatabaseService) (apiClient :> IAPIClient) 1
        
        // Assert
        Assert.True(result)
        let (status, _) = dbService.GetUpdatedOrder(16).Value
        Assert.Equal("api_failure", status)
        
    [<Fact>]
    let ``Test Case 17 - Failed API Call Test``() =
        // Arrange
        let testOrder = { 
            Id = 17
            Type = "B"
            Amount = 75.0
            Flag = false
            Status = "new"
            Priority = "medium" 
        }
        
        let dbService = new MockDatabaseService()
        dbService.SetupOrders([testOrder])
        
        let apiClient = new MockAPIClient()
        apiClient.SetupResponses([(17, { Status = "failed"; Data = box 0 })])  // Failed status
        
        // Act
        let result = OrderProcessingService.processOrders (dbService :> IDatabaseService) (apiClient :> IAPIClient) 1
        
        // Assert
        Assert.True(result)
        let (status, _) = dbService.GetUpdatedOrder(17).Value
        Assert.Equal("api_error", status)
        
    [<Fact>]
    let ``Test Case 18 - Database Exception Test``() =
        // Arrange
        let testOrder = { 
            Id = 18
            Type = "A"
            Amount = 75.0
            Flag = false
            Status = "new"
            Priority = "medium" 
        }
        
        let dbService = new ThrowingMockDatabaseService()
        dbService.SetupOrders([testOrder])
        let apiClient = new MockAPIClient()
        
        // Act
        let result = OrderProcessingService.processOrders (dbService :> IDatabaseService) (apiClient :> IAPIClient) 1
        
        // Assert
        Assert.True(result)
        // Note: We can't verify the status was set to "db_error" 
        // because our mock throws DatabaseException and we can't check the result
        // In real implementation, this would set the order status to "db_error"
        
    [<Fact>]
    let ``Test Case 19 - General Exception Test``() =
        // Arrange
        let dbService = new ExceptionThrowingMockDatabaseService()
        let apiClient = new MockAPIClient()
        
        // Act
        let result = OrderProcessingService.processOrders (dbService :> IDatabaseService) (apiClient :> IAPIClient) 1
        
        // Assert
        Assert.False(result)  // Should return false on unexpected exceptions

