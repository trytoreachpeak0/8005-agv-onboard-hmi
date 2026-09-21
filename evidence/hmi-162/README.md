# onboard-hmi#162 红绿证据

`WireToGateBusinessService` 里两个进程内状态——`_recoveryAnnouncedAttemptId`（这条恢复宣告本进程已经发过）
与 `_owedRecoveryEntry`（因为别的操作占着屏幕而欠着的那条恢复入口）——**绝不能跨重启存活**。
一旦存活，重启后恢复流程会以为宣告已经发过、什么都不发，恢复入口就从操作员屏幕上消失。
那正是 hmi#109 的死法：CI 与单元测试全绿，只有真装置的恢复场景看得见。

在本票之前，这条红线只靠两张票的警示和一段注释。现在由
`tests/SQCD.Agv.UnitTests/RecoveryMarkerPersistenceArchitectureTests.cs` 守着——**本票的价值就是把这条
红线从「只有真装置看得见」挪到 CI 能看见的地方**，在下面「守不到什么」一节列出的范围之内。

本目录是**第四轮复审之后**的版本。第一轮审查确认产品代码是纯重构、守卫有三处中等问题；第二轮复审找到
字段白名单的三种绕法；第三轮复审找到源码读法本身的四个洞（成员名切不出来、字符串里的 `//` 被当注释）；
第四轮复审找到成员切分的一个洞（表达式体成员在 `}` 处被切断，今天就有 3 处）和三处中等问题。
下面四节分别写。

## 一个标记要跨重启存活，需要两步；两步都守

先写到盘上，重启时再读回到字段里。只写不读是无害的（attempt id 本来就在 journal 里），而读回**这个字段**
必定要经过一次对它的写。

| 守卫 | 守哪一侧 | 判据 |
| --- | --- | --- |
| `EachMarkIsWrittenOnlyByItsOneWriter` | 读回 | 每个字段只有一个写入口。写的形状认：简单赋值、复合赋值、解构赋值、`ref`／`out` 传出、括号包住的赋值，以及**拆成两行的赋值与 `ref`** |
| `EveryWayToSetAMarkHasARegisteredCaller` | 读回 | 能写标记的六个方法，每个调用它们的方法都登记在案并写明理由。**一个还没登记为调用方的方法去调写入口会红**；已登记方法里多一处调用不会（见盲区） |
| `EverySetterThatHandsBackAValueIsRegisteredAsOne` | 写出 | **第三轮新增。** 写入口的返回值不是 `void`／`bool`、或者带 `out`／`ref` 参数，就能把标记交还给调用方，必须登记为「交还标记」，调用方随之按持有标记检查 |
| `TheMarksAndTheirSettersNeverLeaveTheBusinessServiceSource` | 写出 | 两个字段名和六个方法名在 `src/`、`tools/` 产品文件里只出现在 `WireToGateBusinessService.cs` |
| `OnlyRegisteredMembersTouchTheMarks` | 写出 | 文件内碰字段的成员恰好是登记的那几个 |
| `MembersThatTouchAMarkReachOnlyTheirListedFields` | 写出 | 从持有标记的成员出发（含经写入口交还值持有标记的成员），沿**代码里点名调用的本类成员**（直接写名字、`this.`、或本类类名限定）一层层走到闭合，闭包里用到的字段（含另一个实例的 `service._x`）必须恰好是白名单里的九个。`_executor`／`_vectorExecutor`／`_session` 这类能把值带出进程的字段，**沿这些路径**用不到；经事件、委托、别的类型、另一个实例上的方法调用走出去的路径不在闭包里（见盲区） |
| `NoMemberThatTouchesAMarkNamesAPersistenceApi` | 写出 | 同一个闭包里不出现持久化 API 的词（不区分大小写）；主要负责白名单看不见的静态 API（`File.`、`JsonSerializer`） |
| `TheLexerNamesEveryMemberOfTheClass` | 自检 | **第三轮新增，第四轮加强。** 四个类文件都能读：花括号深度在文件末尾回到 0，每个成员都切得出名字；遇到原始字符串 `"""` 直接红并说明要先扩展词法层。**深度与命名这两条拦不住深度 1 上的错切**（两半都有名字、深度照样归零），所以第四轮加了一条：没有成员以续行记号（`\|\|`、`?`、`:`、`.` 等）开头；并把今天被切错的 3 处钉成完整成员 |
| `TheseGuardsTellALeakFromTheCodeAsItIs` | 自检 | 每条判据都是源码文本的纯函数，喂合成源码双向验证，外加钉住的已知盲区 |

**没有一条断言是计数。** 每张登记表都与源码做双向集合比较：多了会红，登记了但代码里已经没有了也会红。

## 审查改了什么

审查确认**产品代码是纯重构**：去注释后非注释改动 5 处，`TryClaim` 的比较加写入仍在同一把锁里一步完成，
没有一步变两步，也没有锁内变锁外。问题都在守卫上：

**M-2：词表认不出这个文件真正写盘的方式。** 这个类里**没有 `_journal` 字段**。它真正写盘走的是
`_executor.MarkResultRecordedAsync`／`RecordPendingResultAsync`（背后是 journal）、`_session.Send*Async`
（发件箱，服务端还能重放回来），以及 `_session.Journal.*`。前两种名字里一个词表里的词都没有；我当时的
合成泄漏写的是 `_journal.Write...`，一个本仓根本没有的写法。审查在登记成员里把标记交给
`_executor.MarkResultRecordedAsync`，6 条全绿。

**这和 #176 第一版是同一个错，只是换了一条判据**：#176 那次没认出被守文件自己写字段的方式，这次没认出
被守文件自己写盘的方式。教训被用在了上次出问题的那个位置，没有被用在上次出问题的那个形状上。
所以这次：

- 不是把 `_executor`、`_session` 补进黑名单（那会对下一个接收者重犯），而是**改成白名单**：登记成员只能用
  列出的六个字段，用了任何别的字段就红（第二轮复审证明这一版并不封闭，改为跟调用走的九个字段，见下一节）；
- **对每一条判据都按被守文件的真实写法补了合成例**：写字段（跨行）、写盘（`_executor.`、`_session.Send`、
  `_session.Journal`）、调用写入口（`if (!TryClaim...(...))`、`_ = Exchange...(null)`）、读字段（`?.Context`）。

**M-1：「启动时恢复标记必然是一个新调用方」不成立。** 登记粒度是「哪个方法调用了哪个写入口」这一对，
**已登记方法里多一处调用，这一对本来就在表里，不会红**。而最接近 hmi#109 的恰好是已登记的方法：
`RestorePendingRecoveryOperationProjectionAsync` 本来就拿 journal 里的 attempt id 去占宣告权。
细化到调用点只能靠给调用编号，那是计数，所以没做；**把「必然」改成了它实际保证的范围**（还没登记的方法去调会红），
并把这个盲区钉进合成自检，红证据 `08` 在真实文件上确认它确实看不见。

**M-3：跨两行的赋值看不见。** 现在认字段在行尾、下一行以赋值运算符开头，以及 `ref`／`out` 在行尾、
下一行以字段开头这两种；以 `==` 开头的下一行（比较）不算。拆成多行的解构赋值仍然看不见，写进了限度。

**Q-1**：名字边界的报错现在告诉人正确做法（把访问挪回主文件里已受检查的成员），并明说**不要把第二个
文件加成「家」**——其余每条守卫都只读主文件，第二个家会一下子失去它们全部，而且全绿。

**Q-2**：「读回必定经过一次写」只对**字段本身**成立。同一种失效可以完全不经过字段：往 journal 里记一个
「已宣告」标志、重启后在恢复流程里提前 return。这里看不见它，已写进限度。

**Q-3**：登记一个只返回字段值的读取器，等于开了一个口子；报错只能提醒，拦不住，已写进限度。

## 第二轮复审改了什么

**严重：字段白名单能被三种写法绕过**，都能编译、format 通过、本类全绿：

- **S-1**：`this._executor.MarkResultRecordedAsync(...)`。字段的正则排除了点号后面的写法，`this.` 也被排除了。
- **S-2**：一行调用本类现成的 `RecordAcknowledgedCompletedResultAsync`，它内部就写 journal。白名单只看每个成员
  自己的行，不跟调用——**而它自己的报错还建议「把工作挪到另一个不碰标记的成员里做」，照做就是这一种**。
- **S-3**：`OweRecoveryEntry` 从 `ExchangeOwedRecoveryEntry` 的返回值拿到标记（`displaced`），代码里不写字段名，
  不在检查范围里。

S-2 是根本性的。复审给了两条路：(a) 跟着本类调用算闭包；(b) 做不到就把措辞收窄、盲区钉住。**选了 (a)**：
动手前先用原型实测闭包大小——从持有标记的成员出发只到 9 个成员、9 个字段，新增的三个字段
（`_currentOperationSnapshot`、`_expectedActionWait`、`_operatorEventDeduplicator`）逐个核过都只在内存，
不会引出成片误报。

- 字段识别允许 `this.`；
- 从持有标记的成员出发，沿本类成员调用走到闭合，四个 partial 文件都读；`nameof(...)` 与 `new X(` 不算调用；
- 持有标记的成员 = 碰字段的成员 + 调用了「交还标记的写入口」的成员；
- 类文件集合与 `src/` 下所有声明 `partial class WireToGateBusinessService` 的文件做集合比较（不是计数）；
- 报错改成：不要把标记或碰标记的工作交给本类另一个**被点名调用的**成员——那会被跟进去；交给事件或别的类型
  不会被跟进去，那是这条检查漏掉的泄漏，不是修法（第三轮收窄后的措辞）。

**闭包止于代码里点名的本类成员**，写进了限度并钉住：交给**别的类型**的方法、以及 `OperatorEventPublished` 的订阅方
（今天是界面，和一个只读 journal 的刷新）都不跟。原来那条「名字无害的方法」盲区，如果那个方法是本类的、
并且被直接点名调用，现在会红（红证据 10）；别的类型的方法、经事件到达的处理方法还是盲区。

**中等**：

- **C-1**：上一版八份 `.patch` 仍混着 M-1 的注释改动，`git apply --check` 对当时的 head 全部失败，README 却说
  「只剩注入本身」——和再上一版同一个错，diff 对的是旧 HEAD。从这一版起先提交代码、确认 `src/` 与 HEAD 一致再注入，
  每份 patch 生成后在干净的树上跑 `git apply --check`。
- **C-2**：`ExchangeOwedRecoveryEntry` 注释里「新的置位方式必然是新调用方」同样是 M-1 的过度声称，已收窄（只改注释）。
- **C-3**：括号包住的赋值 `(_recoveryAnnouncedAttemptId) = ...` 三种写入形状都不认，补上；括号里的比较不算。

另外 `Setters` 的定义原先写的是「直接或间接设置标记的成员」，按字面四个调用方也算。现在定义改为
「写入口，以及唯一职责是置位或清除标记的辅助方法」，并写明登记表为什么停在往上一层：那四个调用方正是
「本进程此刻要宣告这一条」这个决定发生的地方。

## 第三轮复审改了什么

第三轮找到的四个严重问题，根子是同一个：**守卫读源码的方式太粗**。它按行、按正则读，成员名取的是「第一个
左括号前的那个词」，注释按「行里第一个 `//`」切掉。于是：

- **元组返回的方法切不出名字**：`private (bool Recorded, string AttemptId) RecordPaid(...)` 第一个 `(` 在返回类型里，
  取到的名字不对，闭包跟不进去（红证据 `13`，上一版全绿）。
- **表达式体属性、单行属性、索引器没有名字**：`Executor =>\n _executor;` 没有左括号，成了无名成员，
  对它的调用跟不进去（红证据 `14`，上一版全绿）。
- **`out` 参数绕过「交还标记」登记**：自检只看返回类型；写入口带一个 `out OwedRecoveryEntry?` 参数就能把标记
  交出去（红证据 `15`，上一版全绿）。
- **字符串里的 `//` 被当成注释**：`"onboard://recovery/" + attemptId, out _recoveryAnnouncedAttemptId` 后半行被切掉，
  写入看不见（红证据 `16`）；在另一个 partial 文件里同样的写法连名字边界也绿（红证据 `17`）。`/*` 与 `*/`
  落在字符串里也是同一类问题。

**这次按调度的决定做了一层真正的词法分析**，而不是再补一条正则。不引入 Roslyn：那是新依赖，要动工具链基线。

- **词法层**（`Lex`）按 C# 规则读：普通字符串（含转义）、字符字面量、`@"…"` 逐字字符串（`""` 不结束它）、
  `$"…"`／`$@"…"`／`@$"…"` 插值字符串（插值洞里是代码，按代码继续读，里面可以再嵌字符串）、`//` 与 `/* */` 注释、
  预处理行。输出两份等长视图：一份把字面量和注释都换成等长空白（判写入、切成员、跟调用用它），一份只把注释换掉、
  保留字符串（名字边界用它，反射按字符串读字段也看得见）。
- **原始字符串 `"""` 明确拒绝**，报错写明「守卫不读原始字符串，先扩展词法层」。今天四个类文件里没有；将来加进来，
  `TheLexerNamesEveryMemberOfTheClass` 会带着这句报错红，而不是悄悄读错。名字边界要扫的 `src/`、`tools/` 里
  其它文件有用原始字符串的（例如 `SqliteWireToGateJournal.cs`），那些文件退回按原文检查，只会多报不会漏报。
- **每个文件都断言花括号深度最终回到 0**（M-2），深度不对就拒绝切成员，不带着错的切法往下走。
- **成员在类深度 1 的边界切**，名字取「第一个 `(`、`=>`、`{`、`;`、`[` 之前的最后一个标识符」，索引器叫 `this[`；
  自检断言四个文件里没有一个无名成员。（第四轮发现：这条自检与深度归零都拦不住深度 1 上的错切，见下一节。）
- **「交还标记」自检扩展到 `out`／`ref` 参数**（严重-3）。

**中等与提问**：

- **M-1：本类自己的事件与委托不跟。** 持有标记的成员 `RecoveryEntryPaid?.Invoke(mark)`，构造函数里挂上的处理方法
  把它写进 journal——闭包跟着名字走到事件的声明，走不到处理方法挂在哪里。按调度的决定写进限度、钉进合成自检，不去追。
- **M-3：收窄了几处过度声称**：README 与 PR 正文里「够不着」「交给本类的方法现在会红」改成「沿闭包跟的路径」
  「被直接点名调用的本类方法」；测试里「cannot be missed」改为写明只看返回值与 `out`／`ref`；两条报错不再说
  「调用会被跟进去」而不加限定，并明说「挪到事件或别的类型后面不是修法，是这条检查漏掉的泄漏」；
  `_recoveryAnnouncedAttemptId` 的 XML doc 从「从新方法恢复会红」改为「还没登记为调用方的方法去调写入口会红」。
- **Q-1：`void`／`bool` 是前提不是事实。** 返回 `bool` 的写入口今天只回答「占到没有」，放行它靠的是这个前提，
  已在 `HandsOutAValue` 旁写明。

## 第四轮复审改了什么

第四轮复审确认第三轮的四个严重问题、Q-2、M-2 都会变红，词法层本身经受了二十多种写法的攻击。剩下**一个严重项**：

**严重-A：表达式体成员在 `}` 处被静默切断。** 上一版按行切：深度 1 上某行的代码以 `}` 或 `;` 结尾，成员就在这一行结束。
于是一行以属性模式 `is { … }`、`with { … }` 或对象初始化器结尾、下一行以 `||`、`&&`、`?`、`:`、`??` 续写的表达式体成员，
被切成两个成员，后半截还拿到一个像样的名字（`Read`、`null`、`MarkResultRecordedAsync`）。「每个成员都有名字」与
「深度归零」两条自检都拦不住。**这个类今天就有 3 处被切错**：主文件的 `RecoveryReasonAlreadyGiven`（后半截叫 `Read`），
`.RecoveryVectors.cs` 的 `ForgetRefusedVector` 与 `PersistedOperatorOrNull`（后半截都叫 `null`）。修正前后，真实类上的字段白名单
都是同样九个字段、都绿，所以今天的代码没有因此漏报；但同样形状的成员如果持有标记并写 journal，后半截没人跟进
（红证据 `18`～`21`，上一版全绿）。反过来也有怪事：上一版里叫 `null` 的后半截会被任何出现 `null` 的成员「调用」到，
第三轮红证据 13 的 reached through 列表里就有 `null`。

- **切分改为逐字符判断**（按复审建议）：深度 1 上遇到 `;` 结束；从深度 2 回到 1 的 `}`，只有在这个成员此前没有出现自己的
  `=` 或 `=>`、且后面紧跟的不是 `=`（自动属性初始化器 `{ get; } = x;`）时才结束。方法、嵌套类型、带访问器的属性在 `}` 结束，
  表达式体成员与字段初始化器一直走到 `;`。
- **续行记号自检**：没有成员以 `||`、`&&`、`?`、`:`、`.`、`??`、`,` 等开头，也不以 `is`／`with`／`switch` 这类模式关键字开头。
  声明永远不会这样开头，所以这条专门接住深度与命名两条看不见的错切。
- **今天那 3 处钉成回归用例**：断言它们各自只有一个跨度、跨度一直到 `;`、并包含原来被切掉的那半截；类里不再有叫 `Read`、`null` 的成员。
- **读回侧变体**：后半截恰好叫 `TryClaimRecoveryAnnouncement` 时，原来只被 `HandsOutAValue` 里的 `.Single` 偶然挡住，
  报 `Sequence contains more than one matching element`，毫无指引（红证据 `22` 现在红在调用方登记那条）。同名写入口声明不止一次
  现在给出明确报错：列出行号，说明是重载（要单独命名登记）还是切分出错。

**中等**：

- **中等-1**：写入口带委托类型参数（`Action<OwedRecoveryEntry?> handBack`，里面 `handBack(Exchange…(null))`）也能把标记交还调用方。
  「交还标记」自检现在把按名字认得出的委托类型参数也算上（`Action`、`Func`、`…Callback`、`…Handler` 等；红证据 `23`）。
- **中等-2**：另一个实例的字段（静态成员里 `service._executor…`）原来认不出。现在字段识别不再排除前面的点号（红证据 `24`）。
  **同一机理在另外两处也有落点**，一并改了：静态成员写 `service._owedRecoveryEntry = null`（红证据 `26`），以及调用
  `service.MarkRecoveryAnnounced(...)`（红证据 `27`）。标记名与写入口名只允许出现在这个文件里，所以前面带点号一定指本类。
- **中等-3**：以类名限定调用本类静态成员 `WireToGateBusinessService.ArchiveMark(...)` 原来不跟进，现在跟（红证据 `25`）。
  **另一个实例上的方法调用**（`service.RecordAcknowledged…()`）仍然不跟：`x.Name` 可能是别的类型的同名成员。已写进限度。
- **中等-4**：文档收窄。测试注释、class remarks、PR 正文都写明「深度与命名自检拦不住深度 1 的错切，由续行记号自检兜」；
  「代码里点名的成员会被跟进去」按修复后的实际能力写（直接写名字、`this.`、本类类名）。
- **中等-5**：「产品文件非注释改动为 0」补上起点，见下面「写入收口」一节。

**疑问三条写进限度**：同一行两个成员共用这一行；用转义写的标识符（`@_executor`、`\u005Fexecutor`）认不出；
`@""""` 与 `$"{d:dd'}"` 这类合法字面量会被误拒（大声失败，不会悄悄读错；类里今天没有）。

## 「只扫一个文件」站得住的条件，逐条核过

| 条件 | #176（`MainViewModel`） | 本票（`WireToGateBusinessService`） |
| --- | --- | --- |
| 字段只有类自己能碰 | 属性 `private set` | 字段 `private` ✓ |
| 类只有一个文件 | 非 partial ✓ | **partial，另有三个文件** ✗ —— 名字边界那条接住它（红证据 `03`、`17`） |
| 没有子类 | `sealed` ✓ | `sealed` ✓ |
| XAML 写不进来 | 只单向绑定 | 私有字段不能绑定 ✓ |
| 没有反射 | ✓ | `src/`、`tools/` 无 `BindingFlags.NonPublic`／`GetField`／`UnsafeAccessor` ✓ |
| 没有源生成器参与 | — | ✓ |

## 写入收口（产品代码改动，逐条；审查核过是纯重构）

- `TryClaimRecoveryAnnouncement`：直接写字段改为 `MarkRecoveryAnnounced(attemptId)`，仍在同一把锁里（`lock` 可重入）。
- 新增 `ExchangeOwedRecoveryEntry(next)`：`_owedRecoveryEntry` 的一处赋值和两处置 null 都改走它。
  **合成一个入口的理由**：置 null 就是还掉欠账，而 hmi#156 数漏的恰好是欠账的释放点。

两个字段的 XML doc 写明了为什么不能持久化、失效时长什么样，指向 hmi#109 和这个测试类。

**产品代码的改动量，带起点说**（第四轮中等-5）：

- **自 `a776582`（本票第一个提交）起，非注释改动为 0。** 之后四轮审查对产品文件只改了注释：`_recoveryAnnouncedAttemptId`
  的 XML doc 与 `ExchangeOwedRecoveryEntry` 的 remarks，都是把过度声称收窄到实际保证的范围。
- **相对基分支 `w2g/fp-v2-impl`，非注释、非空的改动是 22 行（加 13 行、删 9 行）**，就是上面两条收口重构本身。
  算法：`git diff -U0 origin/w2g/fp-v2-impl HEAD -- src/` 的 `+`／`-` 行，去掉 `//`、`///` 开头的行与空行。

## self/：守卫自身的反向验证（第四轮新增）

真实文件上的注入（下一节）证明守卫认得出泄漏；这里反过来证明**本轮对守卫的每一处修改都有测试在看着**：临时把一处修改
退回旧写法，看预期的那条测试是否变红，而且红在预期的那条断言上。每份开头有预期、对 HEAD 的 diff、编译结果
（`0 Error(s)`，编不过就作废），末尾列出失败断言所在的行。八份实际结果全部与预期一致；还原后测试文件的 blob 与 HEAD 一致。

| 文件 | 退回的修改 | 预期（事先写下） | 实际 |
| --- | --- | --- | --- |
| `S1-line-based-member-cut-restored` | 成员切分退回上一版的按行规则 | 命名自检红在**续行记号**那条（不是无名成员那条）+ 合成自检红在严重-A 第一例 | 正是这两处；报错列出主文件第 285 行 `Read` |
| `S2-…-without-the-continuation-check` | S1 再去掉续行记号断言 | 命名自检仍红，红在回归钉 + 合成自检红 | 正是这两处：`RecoveryReasonAlreadyGiven` 的跨度里找不到后半截 |
| `S3-…-without-continuation-check-or-pins` | S2 再去掉回归钉 | 命名自检**绿**（深度与命名确实看不见错切）；只有合成自检红在严重-A 第一例 | 一致 |
| `S4-delegate-parameter-not-a-hand-back` | 委托类型参数不算交还 | 只有合成自检红，红在中等-1 用例 | 一致 |
| `S5-field-after-a-dot-not-seen` | 字段识别重新排除点号前缀 | 只有合成自检红，红在 `service._executor` 用例 | 一致 |
| `S6-class-name-qualified-call-not-followed` | 闭包不再跟进类名限定调用 | 只有合成自检红，红在中等-3 用例 | 一致 |
| `S7-dotted-setter-call-not-seen` | 写入口调用重新排除点号前缀 | 只有合成自检红，红在 `service.MarkRecoveryAnnounced` 用例 | 一致 |
| `S8-dotted-mark-write-not-seen` | 标记写入重新排除点号前缀 | 只有合成自检红，红在 `service._owedRecoveryEntry = null` 用例 | 一致 |

S1 第一版探针写错过一次，记在这里：它在行中间遇到 `}` 也收尾，于是把 `{ get; } = x;` 切出一个无名成员，红在「无名成员」
那条上。那证明不了续行记号断言能挡住旧切法，因为它没有照旧切法的样子失败。改成忠实的旧规则（`}`、`;` 只在行尾收尾）后重跑，
才红在续行记号那条上。

## red/：真实文件上的反向验证（第四轮复审后的守卫，全部重跑）

每份开头记着注入时的 HEAD、注入前的 blob、**事先写下的预期**、命令与限度；同名 `.patch` 只含那次注入。
先提交代码、确认 `src/` 与 HEAD 一致再注入，每份 patch 生成后在干净的树上用 `git apply --check` 验证，
**二十七份全部通过**。还原用按字节的备份，二十七次之后 `src/` 与 HEAD 一致。`01`～`17` 是前三轮的注入，按最终版本重跑；
`18`～`27` 是第四轮新增。实际结果**二十七份全部与预期一致**。

| 文件 | 注入 | 预期（事先写下） | 实际 |
| --- | --- | --- | --- |
| `01-write-outside-the-writer` | `ReleaseInFlightAttempt` 里直接写 `_owedRecoveryEntry = null;` | 写入口那条 + 成员登记那条 | 9 条里红 2 条，正是这两条 |
| `02-writer-hands-the-mark-to-a-serializer` | 写入口里 `JsonSerializer.Serialize(字段)` | 只有持久化 API 那条 | 9 条里红 1 条 |
| `03-other-partial-file-reads-the-mark` | `.HardwareRecovery.cs` 里读字段 | 只有名字边界那条 | 9 条里红 1 条 |
| `04-new-caller-restores-the-mark` | 新方法 `RestoreRecoveryMarkAtStartup` 调写入口 | 只有调用方登记那条 | 9 条里红 1 条 |
| `05-ref-write-replaces-the-writer` | `Interlocked.Exchange(ref 字段, null)` 取代写入口 | 写入口那条 + 调用方登记那条 | 9 条里红 2 条，正是这两条 |
| `06-mark-handed-to-the-executor-journal` | 审查 m3b：登记成员里 `_executor.MarkResultRecordedAsync(标记, ...)` | 只有字段白名单那条 | 9 条里红 1 条 |
| `07-write-split-over-two-lines` | 审查 m6b：登记成员里跨两行写标记 | 只有写入口那条 | 9 条里红 1 条 |
| `08-silent-claim-in-a-registered-method-known-blind-spot` | 审查 m5：`RestorePending…` 里先无声地占住宣告权 | **全绿**：钉住的已知盲区 | 9 条全绿 |
| `09-this-prefixed-executor` | 复审 S-1：`this._executor.MarkResultRecordedAsync(...)` | 只有字段白名单那条 | 9 条里红 1 条 |
| `10-call-to-an-existing-journal-writing-method` | 复审 S-2：一行调用本类现成的 `RecordAcknowledgedCompletedResultAsync` | 字段白名单那条 + 持久化 API 那条 | 9 条里红 2 条，正是这两条 |
| `11-mark-held-through-exchange-return-value` | 复审 S-3：`OweRecoveryEntry` 把 `displaced` 交给 `_executor` | 只有字段白名单那条 | 9 条里红 1 条 |
| `12-parenthesised-write` | 复审 C-3：`(_recoveryAnnouncedAttemptId) = ...` | 只有写入口那条 | 9 条里红 1 条 |
| `13-tuple-returning-method-writes-the-journal` | 第三轮严重-1：元组返回的新方法 `RecordPaid` 写 journal | 只有字段白名单那条 | 9 条里红 1 条 |
| `14-expression-bodied-property-reaches-the-executor` | 第三轮严重-2：换行的表达式体属性 `Executor =>\n _executor;` | 只有字段白名单那条 | 9 条里红 1 条 |
| `15-out-parameter-hands-the-mark-back` | 第三轮严重-3：`ForgetOwedRecoveryEntry` 加 `out` 参数 | 只有「交还标记」登记那条 | 9 条里红 1 条 |
| `16-double-slash-inside-a-string-hides-a-write` | 第三轮严重-4：`Parse("onboard://recovery/" + attemptId, out _recoveryAnnouncedAttemptId)` | 写入口那条 + 成员登记那条 | 9 条里红 2 条，正是这两条 |
| `17-double-slash-inside-a-string-in-another-partial-file` | 第三轮严重-4 另一处：`.HardwareRecovery.cs` 里 `"onboard://recovery/" + 字段` | 只有名字边界那条 | 9 条里红 1 条 |
| `18-property-pattern-then-conditional-reaches-the-executor` | **第四轮严重-A 原复现**：`RecordPaidEntryAsync(owed) =>` `owed is { … }` 换行 `? _executor.MarkResultRecordedAsync(...)` `: Task.CompletedTask;`，`PublishOwedRecoveryEntry` 调它（上一版全绿） | 只有字段白名单那条 | 9 条里红 1 条 |
| `19-property-pattern-then-or-reaches-the-executor` | **严重-A 变体**：`is { … }` 换行 `\|\| _executor…`（上一版全绿） | 只有字段白名单那条 | 9 条里红 1 条 |
| `20-property-pattern-then-and-reaches-a-file-api` | **严重-A 变体**：`is { … }` 换行 `&& … System.IO.File.AppendAllText(...)`（上一版全绿） | 只有持久化 API 那条 | 9 条里红 1 条 |
| `21-with-expression-then-coalesce-reaches-the-executor` | **严重-A 变体**：`owed with { … }` 换行 `?? _executor…`（上一版全绿） | 只有字段白名单那条 | 9 条里红 1 条 |
| `22-property-pattern-then-and-claims-the-mark` | **严重-A 读回侧变体**：新方法 `ClaimIfNoVector` 以 `is { … }` 换行 `&& TryClaimRecoveryAnnouncement(...)`（上一版只被 `.Single` 偶然挡住） | 只有调用方登记那条 | 9 条里红 1 条 |
| `23-delegate-parameter-hands-the-mark-back` | **第四轮中等-1**：`ForgetOwedRecoveryEntry` 加 `Action<OwedRecoveryEntry?> handBack` 参数（上一版全绿） | 只有「交还标记」登记那条 | 9 条里红 1 条 |
| `24-another-instances-executor-from-a-static-member` | **第四轮中等-2**：静态成员 `ArchiveMark(service, id) => _ = service._executor.MarkResultRecordedAsync(...)`，`PublishOwedRecoveryEntry` 调它（上一版全绿） | 只有字段白名单那条 | 9 条里红 1 条 |
| `25-static-member-called-through-the-class-name` | **第四轮中等-3**：同上，但以 `WireToGateBusinessService.ArchiveMark(...)` 调用（上一版不跟进） | 只有字段白名单那条 | 9 条里红 1 条 |
| `26-another-instances-mark-written-from-a-static-member` | **中等-2 同一机理在写入侧**：静态成员 `service._owedRecoveryEntry = null;`（上一版全绿） | 写入口那条 + 成员登记那条 | 9 条里红 2 条，正是这两条 |
| `27-writer-called-on-another-instance` | **中等-2 同一机理在调用侧**：静态成员 `service.MarkRecoveryAnnounced(id)`（上一版全绿） | 只有调用方登记那条 | 9 条里红 1 条 |

## green/

- `unit-tests.txt` —— `dotnet test tests/SQCD.Agv.UnitTests -c Release`，**513 通过、0 失败**（基线 504，新增 9 条；第四轮的新断言都加在已有的两条自检里，条数不变）
- `dotnet-format-verify.txt` —— `exit=0`，零诊断
- `g2-multi-demand-journey.txt` —— `MultiDemandJourneyG2Tests` 单类 **47 条全过**（第一轮跑的；之后产品代码只改了注释）
- `probe-which-tests-reach-the-writers.txt` —— 临时让两个写入口抛异常，同一个类 **47 条里 7 条红**，证明那 47 条全绿
  确实走到了改动。前两次探针因编译失败作废（跑的是旧二进制），先看 `0 Error(s)` 才发现

## 这组守卫守不到什么（测试类 remarks 里有完整版）

- **已登记方法里多一处调用**（M-1）：登记粒度是方法对写入口；`RestorePending…` 本来就从 journal 取 id。钉在合成自检里，红证据 `08`。
- **闭包只跟代码里点名的本类成员**（直接写名字、`this.`、本类类名限定）：持有标记的成员把它交给**别的类型**的方法去落盘；
  `OperatorEventPublished` 的订阅方把收到的东西存下来；**本类自己的事件或委托**，处理方法在别处挂上去再写盘（第三轮 M-1）；
  **另一个实例上的方法调用** `service.SomeMethod(...)`（第四轮：`x.Name` 可能是别的类型的同名成员）——这些所有守卫都绿，
  前三种钉在合成自检里。另一个实例的**字段** `service._x` 看得见（红证据 `24`）。
- **写入口用返回值、`out`／`ref`、委托类型参数以外的方式交还标记**（存进另一个字段、触发事件、往传进来的容器里放）：
  「交还标记」那条看不见；存进字段会被字段白名单看见，触发事件就是上一条的盲区。委托类型按名字认，名字不像委托的自定义委托认不出。
  放行返回 `bool` 的写入口靠的是「它只回答占到没有」这个前提。
- **不经过字段的同一种失效**（Q-2）：journal 里的「已宣告」标志加恢复时提前 return。
- **登记一个只返回字段值的读取器**（Q-3）只能靠人拒绝。
- **读法本身的限度**：成员跨度按整行算，同一行两个成员共用这一行；用转义写的标识符（`@_executor`、`\u005Fexecutor`）认不出；
  `@""""` 与 `$"{d:dd'}"` 这类合法字面量会被误拒（大声失败）；**原始字符串 `"""` 词法层不读**，出现在类文件里会红着拒绝，
  出现在别的产品文件里那个文件按原文检查。
- 持久化词表对静态 API 是一张表；跨多行的解构赋值看不见。
- `_logger.Write` 按前提放行：技术日志只写不读。

## 不做的

没有改 `InFlightAttemptReleaseArchitectureTests`、恢复流程的判断与时序、协议、服务端、发件箱与日志的持久化结构。
**真装置不需要**：本票不改产品行为，买的是「红线失效时 CI 会红」。
