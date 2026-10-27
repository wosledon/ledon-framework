# Ledon.DataDemo

数据能力示例，覆盖：

- `AddLedonData` 自动发现/建库；
- EF Core + SQLite；
- `IMultiTenant` 查询过滤（`X-Tenant-Id`）；
- `IDataSeeder` 幂等播种；
- `IDataExchange` CSV 导出。

## 运行

```bash
dotnet run --project samples/Ledon.DataDemo
```

打开 <http://localhost:5110/scalar> 或使用：

```bash
curl http://localhost:5110/api/todos -H "X-Tenant-Id: tenant-a"
curl http://localhost:5110/api/todos/all
curl http://localhost:5110/api/todos/export -H "X-Tenant-Id: tenant-a" -o todos.csv
```

数据库文件在 `samples/Ledon.DataDemo/App_Data/data-demo.db`，删除后可重新播种。
