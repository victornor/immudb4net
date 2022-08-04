/*
Copyright 2022 CodeNotary, Inc. All rights reserved.

Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

	http://www.apache.org/licenses/LICENSE-2.0

Unless required by applicable law or agreed to in writing, software
distributed under the License is distributed on an "AS IS" BASIS,
WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
See the License for the specific language governing permissions and
limitations under the License.
*/

using Grpc.Core;

namespace ImmuDB.Tests;

[TestClass]
public class LoginHealthCheckCompactIndexTests : BaseClientIntTests
{

    [TestInitialize]
    public void SetUp()
    {
        BaseSetUp();
    }

    [TestCleanup]
    public async Task TearDown()
    {
        await BaseTearDown();
    }

    [TestMethod("login (with default credentials), healthCheck, logout")]
    public async Task Test1()
    {
        await client!.Login("immudb", "immudb");
        await client!.UseDatabase("defaultdb");
        bool isHealthy = await client.HealthCheck();
        Assert.IsTrue(isHealthy);
        await client.CompactIndex();
        await client.Logout();
    }

    [TestMethod("login with invalid credentials")]
    [ExpectedException(typeof(RpcException))]
    public async Task Test9()
    {
        await client!.Login("immudb", "incorrect_password");
    }
}