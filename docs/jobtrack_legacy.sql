USE [master]
GO
/****** Object:  Database [JobTrack]    Script Date: 28/07/2023 15:49:41 ******/
CREATE DATABASE [JobTrack]
 CONTAINMENT = NONE
 ON  PRIMARY
 WITH CATALOG_COLLATION = DATABASE_DEFAULT
GO
ALTER DATABASE [JobTrack] SET COMPATIBILITY_LEVEL = 100
GO
IF (1 = FULLTEXTSERVICEPROPERTY('IsFullTextInstalled'))
begin
EXEC [JobTrack].[dbo].[sp_fulltext_database] @action = 'enable'
end
GO
ALTER DATABASE [JobTrack] SET ANSI_NULL_DEFAULT OFF
GO
ALTER DATABASE [JobTrack] SET ANSI_NULLS OFF
GO
ALTER DATABASE [JobTrack] SET ANSI_PADDING OFF
GO
ALTER DATABASE [JobTrack] SET ANSI_WARNINGS OFF
GO
ALTER DATABASE [JobTrack] SET ARITHABORT OFF
GO
ALTER DATABASE [JobTrack] SET AUTO_CLOSE ON
GO
ALTER DATABASE [JobTrack] SET AUTO_SHRINK ON
GO
ALTER DATABASE [JobTrack] SET AUTO_UPDATE_STATISTICS ON
GO
ALTER DATABASE [JobTrack] SET CURSOR_CLOSE_ON_COMMIT OFF
GO
ALTER DATABASE [JobTrack] SET CURSOR_DEFAULT  GLOBAL
GO
ALTER DATABASE [JobTrack] SET CONCAT_NULL_YIELDS_NULL OFF
GO
ALTER DATABASE [JobTrack] SET NUMERIC_ROUNDABORT OFF
GO
ALTER DATABASE [JobTrack] SET QUOTED_IDENTIFIER OFF
GO
ALTER DATABASE [JobTrack] SET RECURSIVE_TRIGGERS OFF
GO
ALTER DATABASE [JobTrack] SET  DISABLE_BROKER
GO
ALTER DATABASE [JobTrack] SET AUTO_UPDATE_STATISTICS_ASYNC OFF
GO
ALTER DATABASE [JobTrack] SET DATE_CORRELATION_OPTIMIZATION OFF
GO
ALTER DATABASE [JobTrack] SET TRUSTWORTHY OFF
GO
ALTER DATABASE [JobTrack] SET ALLOW_SNAPSHOT_ISOLATION ON
GO
ALTER DATABASE [JobTrack] SET PARAMETERIZATION SIMPLE
GO
ALTER DATABASE [JobTrack] SET READ_COMMITTED_SNAPSHOT OFF
GO
ALTER DATABASE [JobTrack] SET HONOR_BROKER_PRIORITY OFF
GO
ALTER DATABASE [JobTrack] SET RECOVERY SIMPLE
GO
ALTER DATABASE [JobTrack] SET  MULTI_USER
GO
ALTER DATABASE [JobTrack] SET PAGE_VERIFY TORN_PAGE_DETECTION
GO
ALTER DATABASE [JobTrack] SET DB_CHAINING OFF
GO
ALTER DATABASE [JobTrack] SET FILESTREAM( NON_TRANSACTED_ACCESS = OFF )
GO
ALTER DATABASE [JobTrack] SET TARGET_RECOVERY_TIME = 0 SECONDS
GO
ALTER DATABASE [JobTrack] SET DELAYED_DURABILITY = DISABLED
GO
ALTER DATABASE [JobTrack] SET ACCELERATED_DATABASE_RECOVERY = OFF
GO
ALTER DATABASE [JobTrack] SET QUERY_STORE = OFF
GO
USE [JobTrack]
GO
/****** Object:  FullTextCatalog [JobCat]    Script Date: 28/07/2023 15:49:41 ******/
CREATE FULLTEXT CATALOG [JobCat]
GO
/****** Object:  UserDefinedFunction [dbo].[FindOrigSubst]    Script Date: 28/07/2023 15:49:41 ******/
SET ANSI_NULLS OFF
GO
SET QUOTED_IDENTIFIER OFF
GO
CREATE FUNCTION [dbo].[FindOrigSubst] (@node bigint)
RETURNS bigint AS
BEGIN
-- Find original node this is a subst for, returns NULL on error
-- In case of 10 -> 17 -> 29, given 29 it will return 10.
-- THIS WILL ONLY FOLLOW ONE PATH, THERE COULD BE SEVERAL

declare @i int
declare @b bigint
declare @subst bigint

set @b = 1
set @i = 0
while @b is not null
begin
	set @i = @i + 1
	if @i > 50
		return NULL		-- stuck in a loop?
	set @b = null
	select @b = min(idcode) from dbo.JobNodes where substnode = @node
	if @b is not null
		set @node = @b
end

return @node
END



GO
/****** Object:  UserDefinedFunction [dbo].[GetSubst]    Script Date: 28/07/2023 15:49:41 ******/
SET ANSI_NULLS OFF
GO
SET QUOTED_IDENTIFIER OFF
GO
CREATE FUNCTION [dbo].[GetSubst] (@node bigint)
RETURNS bigint AS
BEGIN
-- Resolve node Substitution, returns NULL on error
-- In case of 10 -> 17 -> 29, given 10 it will return 29.

declare @i int
declare @b bigint

set @b = 1
set @i = 0
while @b is not null
begin
	-- Check for any substitute nodes, and use them instead
	set @i = @i + 1
	if @i > 50
		return NULL		-- stuck in a loop?
	set @b = null
	select @b = substnode from dbo.JobNodes where idcode = @node
	if @b is not null
		set @node = @b
end

return @node
END

GO
/****** Object:  Table [dbo].[JobNodes]    Script Date: 28/07/2023 15:49:41 ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[JobNodes](
	[idcode] [bigint] IDENTITY(1,1) NOT NULL,
	[parentcode] [bigint] NULL,
	[description] [varchar](1000) NOT NULL,
	[expecteddur] [decimal](18, 1) NULL,
	[expectedcost] [money] NULL,
	[needstart] [datetime] NULL,
	[needfinish] [datetime] NULL,
	[postedby] [int] NOT NULL,
	[forwho] [int] NULL,
	[writeup] [varchar](max) NULL,
	[substnode] [bigint] NULL,
	[priority] [int] NOT NULL,
	[whenposted] [datetime] NOT NULL,
	[inuse] [bit] NOT NULL,
	[allowleaves] [bit] NOT NULL,
	[timestamp] [timestamp] NOT NULL,
	[kind] [int] NOT NULL,
 CONSTRAINT [PK_JobNodes] PRIMARY KEY CLUSTERED
(
	[idcode] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, FILLFACTOR = 90, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY] TEXTIMAGE_ON [PRIMARY]
GO
/****** Object:  Table [dbo].[WorkRecs]    Script Date: 28/07/2023 15:49:41 ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[WorkRecs](
	[idcode] [bigint] NOT NULL,
	[workby] [int] NULL,
	[whenstarted] [datetime] NOT NULL,
	[whenfinished] [datetime] NULL,
	[demandach] [varchar](1) NOT NULL,
	[partialcrit] [varchar](300) NULL,
	[fullcrit] [varchar](300) NULL,
	[achieved] [varchar](1) NOT NULL,
	[contsibling] [bit] NOT NULL,
	[timestamp] [timestamp] NOT NULL,
	[identval] [bigint] IDENTITY(1,1) NOT NULL,
	[whenaltered] [datetime] NOT NULL,
 CONSTRAINT [PK_WorkRecs] PRIMARY KEY CLUSTERED
(
	[idcode] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, FILLFACTOR = 90, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY]
GO
/****** Object:  View [dbo].[BranchNodes]    Script Date: 28/07/2023 15:49:41 ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE VIEW [dbo].[BranchNodes]
AS
SELECT     *
FROM         dbo.JobNodes
WHERE     (idcode NOT IN
                          (SELECT     idcode
                            FROM          dbo.WorkRecs))


GO
/****** Object:  Table [dbo].[Users]    Script Date: 28/07/2023 15:49:41 ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[Users](
	[userid] [int] IDENTITY(1,1) NOT NULL,
	[username] [varchar](100) NOT NULL,
	[fullname] [varchar](100) NOT NULL,
	[homenode] [bigint] NULL,
	[viewcosts] [int] NOT NULL,
	[laststart] [datetime] NULL,
	[visible] [bit] NOT NULL,
	[defrate] [money] NULL,
	[allowjobs] [bit] NOT NULL,
	[accesslevel] [int] NOT NULL,
 CONSTRAINT [PK_Users] PRIMARY KEY CLUSTERED
(
	[userid] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, FILLFACTOR = 90, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY]
GO
/****** Object:  View [dbo].[VisUsers]    Script Date: 28/07/2023 15:49:41 ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE VIEW [dbo].[VisUsers]
AS
SELECT     *
FROM         dbo.Users
WHERE     (visible = 1)
GO
/****** Object:  Table [dbo].[NodeAchs]    Script Date: 28/07/2023 15:49:41 ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[NodeAchs](
	[idcode] [bigint] NOT NULL,
	[ach] [int] NOT NULL,
	[ach2] [varchar](1) NOT NULL,
 CONSTRAINT [PK_NodeAchs] PRIMARY KEY CLUSTERED
(
	[idcode] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, FILLFACTOR = 90, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY]
GO
/****** Object:  View [dbo].[InvalidAchLookup]    Script Date: 28/07/2023 15:49:41 ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE VIEW [dbo].[InvalidAchLookup]
AS
SELECT     dbo.JobNodes.idcode, dbo.JobNodes.description, dbo.NodeAchs.ach2, dbo.IsNodeAch2(dbo.JobNodes.idcode,1) AS LiveAch2
FROM         dbo.JobNodes LEFT OUTER JOIN
                      dbo.NodeAchs ON dbo.JobNodes.idcode = dbo.NodeAchs.idcode
WHERE     (dbo.NodeAchs.idcode IS NULL) OR
                      (dbo.IsNodeAch2(dbo.JobNodes.idcode,1) <> dbo.NodeAchs.ach2)

GO
/****** Object:  View [dbo].[InvalidRequires]    Script Date: 28/07/2023 15:49:41 ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE VIEW [dbo].[InvalidRequires]
AS
SELECT     dbo.JobNodes.idcode AS InvalidNode, dbo.CheckReqNodes(dbo.JobNodes.idcode) AS ReqNode, dbo.WorkRecs.idcode AS ReqWorkRec
FROM         dbo.JobNodes LEFT OUTER JOIN
                      dbo.WorkRecs ON dbo.CheckReqNodes(dbo.JobNodes.idcode) = dbo.WorkRecs.idcode
WHERE     (dbo.CheckReqNodes(dbo.JobNodes.idcode) IS NOT NULL) AND (dbo.IsNodeAch2(dbo.JobNodes.idcode,1) = 'A')

GO
/****** Object:  Table [dbo].[TreeStore]    Script Date: 28/07/2023 15:49:41 ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[TreeStore](
	[id] [bigint] NOT NULL,
	[child] [bigint] NOT NULL,
	[lev] [int] NOT NULL,
 CONSTRAINT [PK_TreeStore] PRIMARY KEY CLUSTERED
(
	[id] ASC,
	[child] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, FILLFACTOR = 90, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY]
GO
/****** Object:  UserDefinedFunction [dbo].[GetChildrenTree]    Script Date: 28/07/2023 15:49:41 ******/
SET ANSI_NULLS OFF
GO
SET QUOTED_IDENTIFIER OFF
GO

CREATE FUNCTION [dbo].[GetChildrenTree] (@id bigint)
RETURNS table AS
return
	SELECT     * FROM       dbo.TreeStore WHERE     (id = @id)

GO
/****** Object:  UserDefinedFunction [dbo].[GetPathTree]    Script Date: 28/07/2023 15:49:41 ******/
SET ANSI_NULLS OFF
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE FUNCTION [dbo].[GetPathTree] (@id bigint)
RETURNS table AS

-- returns path to root
return
	SELECT     id, lev FROM   dbo.TreeStore WHERE     (child = @id)


GO
/****** Object:  Table [dbo].[AchStatus]    Script Date: 28/07/2023 15:49:41 ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[AchStatus](
	[status] [varchar](1) NOT NULL,
	[orderval] [int] NOT NULL,
	[description] [varchar](50) NOT NULL,
 CONSTRAINT [PK_AchStatus] PRIMARY KEY CLUSTERED
(
	[status] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, FILLFACTOR = 90, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY],
 CONSTRAINT [IX_AchStatusDesc] UNIQUE NONCLUSTERED
(
	[description] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, FILLFACTOR = 90, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY],
 CONSTRAINT [IX_AchStatusOrder] UNIQUE NONCLUSTERED
(
	[orderval] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, FILLFACTOR = 90, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY]
GO
/****** Object:  View [dbo].[Test1]    Script Date: 28/07/2023 15:49:41 ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE VIEW [dbo].[Test1]
AS
SELECT     dbo.JobNodes.idcode, dbo.JobNodes.parentcode, dbo.WorkRecs.demandach, dbo.WorkRecs.achieved, dbo.AchStatus.description,
                      dbo.IsNodeAch2(dbo.JobNodes.idcode,1) AS IsAch, dbo.CheckReqNodes(dbo.JobNodes.idcode) AS ReqNode,
                      dbo.GetFinalEnd(dbo.JobNodes.idcode, NULL) AS DateFinished, dbo.GetFirstStart(dbo.JobNodes.idcode) AS DateStarted,
                      dbo.GetSubst(dbo.JobNodes.idcode) AS SubstNode, CAST(dbo.GetFinalEnd(dbo.JobNodes.idcode, NULL) - dbo.GetFirstStart(dbo.JobNodes.idcode)
                      AS float) AS Duration
FROM         dbo.AchStatus INNER JOIN
                      dbo.WorkRecs ON dbo.AchStatus.status = dbo.WorkRecs.achieved RIGHT OUTER JOIN
                      dbo.JobNodes ON dbo.WorkRecs.idcode = dbo.JobNodes.idcode

GO
/****** Object:  View [dbo].[SiblingParents]    Script Date: 28/07/2023 15:49:41 ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE VIEW [dbo].[SiblingParents]
AS
SELECT     dbo.JobNodes.parentcode AS idcode
FROM         dbo.JobNodes LEFT OUTER JOIN
                      dbo.WorkRecs ON dbo.JobNodes.idcode = dbo.WorkRecs.idcode
WHERE     (dbo.WorkRecs.contsibling = 0) OR
                      (dbo.WorkRecs.contsibling IS NULL)

GO
/****** Object:  Table [dbo].[AchStore]    Script Date: 28/07/2023 15:49:41 ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[AchStore](
	[snapshotid] [int] NOT NULL,
	[node] [bigint] NOT NULL,
	[ach] [bit] NOT NULL,
	[ach2] [varchar](3) NOT NULL,
 CONSTRAINT [PK_AchStore] PRIMARY KEY CLUSTERED
(
	[snapshotid] ASC,
	[node] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, FILLFACTOR = 90, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY]
GO
/****** Object:  Table [dbo].[AchStoreIDs]    Script Date: 28/07/2023 15:49:41 ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[AchStoreIDs](
	[snapshotid] [int] IDENTITY(1,1) NOT NULL,
	[whenused] [datetime] NOT NULL,
	[createdby] [varchar](50) NOT NULL,
 CONSTRAINT [PK_AchStoreIDs] PRIMARY KEY CLUSTERED
(
	[snapshotid] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, FILLFACTOR = 90, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY]
GO
/****** Object:  Table [dbo].[JobsRequired]    Script Date: 28/07/2023 15:49:41 ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[JobsRequired](
	[jobrequired] [bigint] NOT NULL,
	[requiredfor] [bigint] NOT NULL,
 CONSTRAINT [PK_JobsRequired] PRIMARY KEY CLUSTERED
(
	[jobrequired] ASC,
	[requiredfor] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, FILLFACTOR = 90, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY]
GO
/****** Object:  Table [dbo].[LogJobs]    Script Date: 28/07/2023 15:49:41 ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[LogJobs](
	[keyid] [bigint] IDENTITY(1,1) NOT NULL,
	[whenaction] [datetime] NOT NULL,
	[whoaction] [int] NOT NULL,
	[actioncode] [varchar](1) NOT NULL,
	[idcode] [bigint] NOT NULL,
	[parentcode] [bigint] NULL,
	[parentdesc] [varchar](200) NULL,
	[description] [varchar](1000) NULL,
	[writeup] [varchar](max) NULL,
	[priority] [int] NULL,
	[needstart] [datetime] NULL,
	[needfinish] [datetime] NULL,
	[substnode] [bigint] NULL,
	[workident] [bigint] NULL,
	[workby] [int] NULL,
	[whenstarted] [datetime] NULL,
	[whenfinished] [datetime] NULL,
	[demandach] [varchar](1) NULL,
	[partialcrit] [varchar](300) NULL,
	[fullcrit] [varchar](300) NULL,
	[achieved] [varchar](1) NULL,
	[contsibling] [bit] NULL,
 CONSTRAINT [PK_LogWorkRecs] PRIMARY KEY CLUSTERED
(
	[keyid] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, FILLFACTOR = 90, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY] TEXTIMAGE_ON [PRIMARY]
GO
/****** Object:  Table [dbo].[NodeKinds]    Script Date: 28/07/2023 15:49:41 ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[NodeKinds](
	[kindid] [int] NOT NULL,
	[kindname] [varchar](100) NOT NULL,
	[ordercode] [int] NOT NULL,
 CONSTRAINT [PK_NodeKinds] PRIMARY KEY CLUSTERED
(
	[kindid] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, FILLFACTOR = 90, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY],
 CONSTRAINT [IX_NodeKindsNames] UNIQUE NONCLUSTERED
(
	[kindname] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, FILLFACTOR = 90, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY]
GO
/****** Object:  Table [dbo].[NodeRates]    Script Date: 28/07/2023 15:49:41 ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[NodeRates](
	[nodeid] [bigint] NOT NULL,
	[userid] [int] NOT NULL,
	[rateoverride] [money] NOT NULL,
 CONSTRAINT [PK_NodeRates] PRIMARY KEY CLUSTERED
(
	[nodeid] ASC,
	[userid] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, FILLFACTOR = 90, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY]
GO
/****** Object:  Table [dbo].[PriorityLookup]    Script Date: 28/07/2023 15:49:41 ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[PriorityLookup](
	[priority] [int] NOT NULL,
	[description] [varchar](50) NOT NULL,
 CONSTRAINT [PK_PriorityLookup] PRIMARY KEY CLUSTERED
(
	[priority] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, FILLFACTOR = 90, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY]
GO
/****** Object:  Table [dbo].[UserCosts]    Script Date: 28/07/2023 15:49:41 ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[UserCosts](
	[entryid] [int] IDENTITY(1,1) NOT NULL,
	[userid] [int] NOT NULL,
	[startdate] [datetime] NOT NULL,
	[enddate] [datetime] NOT NULL,
	[workrate] [money] NOT NULL,
 CONSTRAINT [PK_UserCosts] PRIMARY KEY CLUSTERED
(
	[entryid] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, FILLFACTOR = 90, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY]
GO
/****** Object:  Table [dbo].[UserSettings]    Script Date: 28/07/2023 15:49:41 ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[UserSettings](
	[userid] [int] NOT NULL,
	[setting] [varchar](20) NOT NULL,
	[val] [varchar](200) NOT NULL,
 CONSTRAINT [PK_UserSettings] PRIMARY KEY CLUSTERED
(
	[userid] ASC,
	[setting] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, FILLFACTOR = 90, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY]
GO
/****** Object:  Table [dbo].[ViewStates]    Script Date: 28/07/2023 15:49:41 ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[ViewStates](
	[hash] [varchar](50) NOT NULL,
	[whenaltered] [datetime] NOT NULL,
	[viewstate] [varchar](max) NOT NULL,
 CONSTRAINT [PK_ViewStates] PRIMARY KEY CLUSTERED
(
	[hash] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, FILLFACTOR = 90, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY] TEXTIMAGE_ON [PRIMARY]
GO
/****** Object:  Table [dbo].[WNodeStore]    Script Date: 28/07/2023 15:49:41 ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE TABLE [dbo].[WNodeStore](
	[groupid] [uniqueidentifier] NOT NULL,
	[nodeid] [bigint] NOT NULL
) ON [PRIMARY]
GO
/****** Object:  Index [IX_AchStoreNode]    Script Date: 28/07/2023 15:49:41 ******/
CREATE NONCLUSTERED INDEX [IX_AchStoreNode] ON [dbo].[AchStore]
(
	[node] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, FILLFACTOR = 90, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
GO
/****** Object:  Index [IX_AchStoreIDsWhen]    Script Date: 28/07/2023 15:49:41 ******/
CREATE NONCLUSTERED INDEX [IX_AchStoreIDsWhen] ON [dbo].[AchStoreIDs]
(
	[whenused] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, FILLFACTOR = 90, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
GO
/****** Object:  Index [IX_JobNodesParent]    Script Date: 28/07/2023 15:49:41 ******/
CREATE NONCLUSTERED INDEX [IX_JobNodesParent] ON [dbo].[JobNodes]
(
	[parentcode] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, FILLFACTOR = 90, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
GO
/****** Object:  Index [IX_JobNodesStart]    Script Date: 28/07/2023 15:49:41 ******/
CREATE NONCLUSTERED INDEX [IX_JobNodesStart] ON [dbo].[JobNodes]
(
	[needstart] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
GO
/****** Object:  Index [IX_JobsRequiredFor]    Script Date: 28/07/2023 15:49:41 ******/
CREATE NONCLUSTERED INDEX [IX_JobsRequiredFor] ON [dbo].[JobsRequired]
(
	[requiredfor] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, FILLFACTOR = 90, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
GO
/****** Object:  Index [IX_LogJobsid]    Script Date: 28/07/2023 15:49:41 ******/
CREATE NONCLUSTERED INDEX [IX_LogJobsid] ON [dbo].[LogJobs]
(
	[idcode] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, FILLFACTOR = 90, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
GO
/****** Object:  Index [IX_LogJobsWork]    Script Date: 28/07/2023 15:49:41 ******/
CREATE NONCLUSTERED INDEX [IX_LogJobsWork] ON [dbo].[LogJobs]
(
	[workident] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, FILLFACTOR = 90, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
GO
/****** Object:  Index [IX_NodeKindsOrder]    Script Date: 28/07/2023 15:49:41 ******/
CREATE UNIQUE NONCLUSTERED INDEX [IX_NodeKindsOrder] ON [dbo].[NodeKinds]
(
	[ordercode] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, IGNORE_DUP_KEY = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, FILLFACTOR = 90, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
GO
/****** Object:  Index [IX_TreeStoreIDLev]    Script Date: 28/07/2023 15:49:41 ******/
CREATE NONCLUSTERED INDEX [IX_TreeStoreIDLev] ON [dbo].[TreeStore]
(
	[id] ASC,
	[lev] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, FILLFACTOR = 90, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
GO
/****** Object:  Index [IX_TreeStoreParent]    Script Date: 28/07/2023 15:49:41 ******/
CREATE NONCLUSTERED INDEX [IX_TreeStoreParent] ON [dbo].[TreeStore]
(
	[child] ASC,
	[lev] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, FILLFACTOR = 90, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
GO
/****** Object:  Index [IX_UserCostsEnd]    Script Date: 28/07/2023 15:49:41 ******/
CREATE NONCLUSTERED INDEX [IX_UserCostsEnd] ON [dbo].[UserCosts]
(
	[enddate] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, FILLFACTOR = 90, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
GO
/****** Object:  Index [IX_UserCostsStart]    Script Date: 28/07/2023 15:49:41 ******/
CREATE NONCLUSTERED INDEX [IX_UserCostsStart] ON [dbo].[UserCosts]
(
	[startdate] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, FILLFACTOR = 90, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
GO
SET ANSI_PADDING ON
GO
/****** Object:  Index [IX_UsersName]    Script Date: 28/07/2023 15:49:41 ******/
CREATE UNIQUE NONCLUSTERED INDEX [IX_UsersName] ON [dbo].[Users]
(
	[username] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, IGNORE_DUP_KEY = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, FILLFACTOR = 90, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
GO
/****** Object:  Index [IX_ViewStatesAltered]    Script Date: 28/07/2023 15:49:41 ******/
CREATE NONCLUSTERED INDEX [IX_ViewStatesAltered] ON [dbo].[ViewStates]
(
	[whenaltered] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, FILLFACTOR = 90, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
GO
/****** Object:  Index [IX_WorkRecsFinish]    Script Date: 28/07/2023 15:49:41 ******/
CREATE NONCLUSTERED INDEX [IX_WorkRecsFinish] ON [dbo].[WorkRecs]
(
	[whenfinished] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, FILLFACTOR = 90, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
GO
/****** Object:  Index [IX_WorkRecsIdent]    Script Date: 28/07/2023 15:49:41 ******/
CREATE UNIQUE NONCLUSTERED INDEX [IX_WorkRecsIdent] ON [dbo].[WorkRecs]
(
	[identval] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, IGNORE_DUP_KEY = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, FILLFACTOR = 90, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
GO
/****** Object:  Index [IX_WorkRecsStart]    Script Date: 28/07/2023 15:49:41 ******/
CREATE NONCLUSTERED INDEX [IX_WorkRecsStart] ON [dbo].[WorkRecs]
(
	[whenstarted] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, FILLFACTOR = 90, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
GO
ALTER TABLE [dbo].[AchStoreIDs] ADD  CONSTRAINT [DF_AchStoreIDs_whenaltered]  DEFAULT (getdate()) FOR [whenused]
GO
ALTER TABLE [dbo].[JobNodes] ADD  CONSTRAINT [DF_JobNodes_priority]  DEFAULT ((3)) FOR [priority]
GO
ALTER TABLE [dbo].[JobNodes] ADD  CONSTRAINT [DF_JobNodes_whenposted]  DEFAULT (getdate()) FOR [whenposted]
GO
ALTER TABLE [dbo].[JobNodes] ADD  CONSTRAINT [DF_JobNodes_inuse]  DEFAULT ((1)) FOR [inuse]
GO
ALTER TABLE [dbo].[JobNodes] ADD  CONSTRAINT [DF_JobNodes_allowleaves]  DEFAULT ((1)) FOR [allowleaves]
GO
ALTER TABLE [dbo].[JobNodes] ADD  CONSTRAINT [DF_JobNodes_kind]  DEFAULT ((0)) FOR [kind]
GO
ALTER TABLE [dbo].[LogJobs] ADD  CONSTRAINT [DF_LogWorkRecs_whenadded]  DEFAULT (getdate()) FOR [whenaction]
GO
ALTER TABLE [dbo].[NodeKinds] ADD  CONSTRAINT [DF_NodeKinds_ordercode]  DEFAULT (0) FOR [ordercode]
GO
ALTER TABLE [dbo].[Users] ADD  CONSTRAINT [DF_Users_viewcosts]  DEFAULT (0) FOR [viewcosts]
GO
ALTER TABLE [dbo].[Users] ADD  CONSTRAINT [DF_Users_visible]  DEFAULT (1) FOR [visible]
GO
ALTER TABLE [dbo].[Users] ADD  CONSTRAINT [DF_Users_allowjobs]  DEFAULT (1) FOR [allowjobs]
GO
ALTER TABLE [dbo].[Users] ADD  CONSTRAINT [DF_Users_accesslevel]  DEFAULT (1) FOR [accesslevel]
GO
ALTER TABLE [dbo].[ViewStates] ADD  CONSTRAINT [DF_ViewStates_whenaltered]  DEFAULT (getutcdate()) FOR [whenaltered]
GO
ALTER TABLE [dbo].[WorkRecs] ADD  CONSTRAINT [DF_WorkRecs_demandach]  DEFAULT ('F') FOR [demandach]
GO
ALTER TABLE [dbo].[WorkRecs] ADD  CONSTRAINT [DF_WorkRecs_achieved]  DEFAULT ('W') FOR [achieved]
GO
ALTER TABLE [dbo].[WorkRecs] ADD  CONSTRAINT [DF_WorkRecs_contsibling]  DEFAULT (0) FOR [contsibling]
GO
ALTER TABLE [dbo].[WorkRecs] ADD  CONSTRAINT [DF_WorkRecs_whenaltered]  DEFAULT (getdate()) FOR [whenaltered]
GO
ALTER TABLE [dbo].[AchStore]  WITH CHECK ADD  CONSTRAINT [FK_AchStore_AchStoreIDs] FOREIGN KEY([snapshotid])
REFERENCES [dbo].[AchStoreIDs] ([snapshotid])
ON UPDATE CASCADE
ON DELETE CASCADE
GO
ALTER TABLE [dbo].[AchStore] CHECK CONSTRAINT [FK_AchStore_AchStoreIDs]
GO
ALTER TABLE [dbo].[JobNodes]  WITH NOCHECK ADD  CONSTRAINT [FK_JobNodes_JobNodes] FOREIGN KEY([parentcode])
REFERENCES [dbo].[JobNodes] ([idcode])
GO
ALTER TABLE [dbo].[JobNodes] CHECK CONSTRAINT [FK_JobNodes_JobNodes]
GO
ALTER TABLE [dbo].[JobNodes]  WITH NOCHECK ADD  CONSTRAINT [FK_JobNodes_JobNodes1] FOREIGN KEY([substnode])
REFERENCES [dbo].[JobNodes] ([idcode])
GO
ALTER TABLE [dbo].[JobNodes] CHECK CONSTRAINT [FK_JobNodes_JobNodes1]
GO
ALTER TABLE [dbo].[JobNodes]  WITH CHECK ADD  CONSTRAINT [FK_JobNodes_NodeKinds] FOREIGN KEY([kind])
REFERENCES [dbo].[NodeKinds] ([kindid])
ON UPDATE CASCADE
GO
ALTER TABLE [dbo].[JobNodes] CHECK CONSTRAINT [FK_JobNodes_NodeKinds]
GO
ALTER TABLE [dbo].[JobNodes]  WITH NOCHECK ADD  CONSTRAINT [FK_JobNodes_PriorityLookup] FOREIGN KEY([priority])
REFERENCES [dbo].[PriorityLookup] ([priority])
ON UPDATE CASCADE
GO
ALTER TABLE [dbo].[JobNodes] CHECK CONSTRAINT [FK_JobNodes_PriorityLookup]
GO
ALTER TABLE [dbo].[JobNodes]  WITH NOCHECK ADD  CONSTRAINT [FK_JobNodes_Users] FOREIGN KEY([postedby])
REFERENCES [dbo].[Users] ([userid])
GO
ALTER TABLE [dbo].[JobNodes] CHECK CONSTRAINT [FK_JobNodes_Users]
GO
ALTER TABLE [dbo].[JobNodes]  WITH NOCHECK ADD  CONSTRAINT [FK_JobNodes_Users1] FOREIGN KEY([forwho])
REFERENCES [dbo].[Users] ([userid])
GO
ALTER TABLE [dbo].[JobNodes] CHECK CONSTRAINT [FK_JobNodes_Users1]
GO
ALTER TABLE [dbo].[JobsRequired]  WITH NOCHECK ADD  CONSTRAINT [FK_JobsRequired_JobNodes] FOREIGN KEY([jobrequired])
REFERENCES [dbo].[JobNodes] ([idcode])
ON UPDATE CASCADE
ON DELETE CASCADE
GO
ALTER TABLE [dbo].[JobsRequired] CHECK CONSTRAINT [FK_JobsRequired_JobNodes]
GO
ALTER TABLE [dbo].[JobsRequired]  WITH NOCHECK ADD  CONSTRAINT [FK_JobsRequired_JobNodes1] FOREIGN KEY([requiredfor])
REFERENCES [dbo].[JobNodes] ([idcode])
GO
ALTER TABLE [dbo].[JobsRequired] CHECK CONSTRAINT [FK_JobsRequired_JobNodes1]
GO
ALTER TABLE [dbo].[NodeAchs]  WITH NOCHECK ADD  CONSTRAINT [FK_NodeAchs_JobNodes] FOREIGN KEY([idcode])
REFERENCES [dbo].[JobNodes] ([idcode])
ON UPDATE CASCADE
ON DELETE CASCADE
GO
ALTER TABLE [dbo].[NodeAchs] CHECK CONSTRAINT [FK_NodeAchs_JobNodes]
GO
ALTER TABLE [dbo].[NodeRates]  WITH NOCHECK ADD  CONSTRAINT [FK_NodeRates_JobNodes] FOREIGN KEY([nodeid])
REFERENCES [dbo].[JobNodes] ([idcode])
ON UPDATE CASCADE
ON DELETE CASCADE
GO
ALTER TABLE [dbo].[NodeRates] CHECK CONSTRAINT [FK_NodeRates_JobNodes]
GO
ALTER TABLE [dbo].[NodeRates]  WITH NOCHECK ADD  CONSTRAINT [FK_NodeRates_Users] FOREIGN KEY([userid])
REFERENCES [dbo].[Users] ([userid])
GO
ALTER TABLE [dbo].[NodeRates] CHECK CONSTRAINT [FK_NodeRates_Users]
GO
ALTER TABLE [dbo].[UserCosts]  WITH NOCHECK ADD  CONSTRAINT [FK_UserCosts_Users] FOREIGN KEY([userid])
REFERENCES [dbo].[Users] ([userid])
ON UPDATE CASCADE
ON DELETE CASCADE
GO
ALTER TABLE [dbo].[UserCosts] CHECK CONSTRAINT [FK_UserCosts_Users]
GO
ALTER TABLE [dbo].[Users]  WITH NOCHECK ADD  CONSTRAINT [FK_Users_JobNodes] FOREIGN KEY([homenode])
REFERENCES [dbo].[JobNodes] ([idcode])
GO
ALTER TABLE [dbo].[Users] CHECK CONSTRAINT [FK_Users_JobNodes]
GO
ALTER TABLE [dbo].[UserSettings]  WITH CHECK ADD  CONSTRAINT [FK_UserSettings_Users] FOREIGN KEY([userid])
REFERENCES [dbo].[Users] ([userid])
ON UPDATE CASCADE
ON DELETE CASCADE
GO
ALTER TABLE [dbo].[UserSettings] CHECK CONSTRAINT [FK_UserSettings_Users]
GO
ALTER TABLE [dbo].[WorkRecs]  WITH NOCHECK ADD  CONSTRAINT [FK_WorkRecs_AchStatus] FOREIGN KEY([achieved])
REFERENCES [dbo].[AchStatus] ([status])
GO
ALTER TABLE [dbo].[WorkRecs] CHECK CONSTRAINT [FK_WorkRecs_AchStatus]
GO
ALTER TABLE [dbo].[WorkRecs]  WITH NOCHECK ADD  CONSTRAINT [FK_WorkRecs_AchStatus1] FOREIGN KEY([demandach])
REFERENCES [dbo].[AchStatus] ([status])
GO
ALTER TABLE [dbo].[WorkRecs] CHECK CONSTRAINT [FK_WorkRecs_AchStatus1]
GO
ALTER TABLE [dbo].[WorkRecs]  WITH NOCHECK ADD  CONSTRAINT [FK_WorkRecs_JobNodes] FOREIGN KEY([idcode])
REFERENCES [dbo].[JobNodes] ([idcode])
ON UPDATE CASCADE
ON DELETE CASCADE
GO
ALTER TABLE [dbo].[WorkRecs] CHECK CONSTRAINT [FK_WorkRecs_JobNodes]
GO
ALTER TABLE [dbo].[WorkRecs]  WITH NOCHECK ADD  CONSTRAINT [FK_WorkRecs_Users] FOREIGN KEY([workby])
REFERENCES [dbo].[Users] ([userid])
GO
ALTER TABLE [dbo].[WorkRecs] CHECK CONSTRAINT [FK_WorkRecs_Users]
GO
ALTER TABLE [dbo].[JobNodes]  WITH NOCHECK ADD  CONSTRAINT [CK_JobNodesFinish] CHECK  (([needstart] IS NULL OR [needfinish] IS NULL OR [needfinish]>[needstart]))
GO
ALTER TABLE [dbo].[JobNodes] CHECK CONSTRAINT [CK_JobNodesFinish]
GO
ALTER TABLE [dbo].[JobNodes]  WITH NOCHECK ADD  CONSTRAINT [CK_JobNodesParent] CHECK  (([parentcode] IS NULL OR [parentcode]<>[idcode]))
GO
ALTER TABLE [dbo].[JobNodes] CHECK CONSTRAINT [CK_JobNodesParent]
GO
ALTER TABLE [dbo].[JobNodes]  WITH NOCHECK ADD  CONSTRAINT [CK_JobNodesSubst] CHECK  (([substnode] IS NULL OR [substnode]<>[idcode]))
GO
ALTER TABLE [dbo].[JobNodes] CHECK CONSTRAINT [CK_JobNodesSubst]
GO
ALTER TABLE [dbo].[JobsRequired]  WITH NOCHECK ADD  CONSTRAINT [CK_JobsRequired] CHECK  (([jobrequired] <> [requiredfor]))
GO
ALTER TABLE [dbo].[JobsRequired] CHECK CONSTRAINT [CK_JobsRequired]
GO
ALTER TABLE [dbo].[NodeRates]  WITH CHECK ADD  CONSTRAINT [CK_NodeRates] CHECK  (([rateoverride] >= 0.0))
GO
ALTER TABLE [dbo].[NodeRates] CHECK CONSTRAINT [CK_NodeRates]
GO
ALTER TABLE [dbo].[UserCosts]  WITH CHECK ADD  CONSTRAINT [CK_UserCostsRate] CHECK  (([workrate] >= 0.0))
GO
ALTER TABLE [dbo].[UserCosts] CHECK CONSTRAINT [CK_UserCostsRate]
GO
ALTER TABLE [dbo].[UserCosts]  WITH CHECK ADD  CONSTRAINT [CK_UserCostsStartEnd] CHECK  (([enddate] > [startdate]))
GO
ALTER TABLE [dbo].[UserCosts] CHECK CONSTRAINT [CK_UserCostsStartEnd]
GO
ALTER TABLE [dbo].[WorkRecs]  WITH NOCHECK ADD  CONSTRAINT [CK_WorkRecsAch] CHECK  (([achieved] = 'W' or ([achieved] = 'F' or [achieved] = 'I' or [achieved] = 'P' or [achieved] = 'N') and [workby] is not null))
GO
ALTER TABLE [dbo].[WorkRecs] CHECK CONSTRAINT [CK_WorkRecsAch]
GO
ALTER TABLE [dbo].[WorkRecs]  WITH NOCHECK ADD  CONSTRAINT [CK_WorkRecsDateRange] CHECK  (([whenstarted] > '1/1/1910' and ([whenfinished] is null or [whenfinished] < '1/1/2100')))
GO
ALTER TABLE [dbo].[WorkRecs] CHECK CONSTRAINT [CK_WorkRecsDateRange]
GO
ALTER TABLE [dbo].[WorkRecs]  WITH NOCHECK ADD  CONSTRAINT [CK_WorkRecsDemand] CHECK  (([demandach] = 'F' or [demandach] = 'P' or [demandach] = 'N'))
GO
ALTER TABLE [dbo].[WorkRecs] CHECK CONSTRAINT [CK_WorkRecsDemand]
GO
ALTER TABLE [dbo].[WorkRecs]  WITH NOCHECK ADD  CONSTRAINT [CK_WorkRecsDemStat] CHECK  (([whenfinished] is null and ([achieved] = 'W' or [achieved] = 'I') or [whenfinished] is not null and ([achieved] = 'N' or [achieved] = 'P' or [achieved] = 'F')))
GO
ALTER TABLE [dbo].[WorkRecs] CHECK CONSTRAINT [CK_WorkRecsDemStat]
GO
ALTER TABLE [dbo].[WorkRecs]  WITH NOCHECK ADD  CONSTRAINT [CK_WorkRecsStartFin] CHECK  (([whenfinished] is null or [whenfinished] >= [whenstarted] and [workby] is not null))
GO
ALTER TABLE [dbo].[WorkRecs] CHECK CONSTRAINT [CK_WorkRecsStartFin]
GO
/****** Object:  StoredProcedure [dbo].[FAddCosts]    Script Date: 28/07/2023 15:49:41 ******/
SET ANSI_NULLS OFF
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE PROCEDURE [dbo].[FAddCosts] (@userid int, @start datetime, @end datetime, @cost money, @repeats int, @lastend datetime output) AS

-- add cost entries for a specific number of days, at same time

declare @i int

if @cost is null
begin
	select @cost = defrate from dbo.Users where userid = @userid
end

set @i = 0
while @i < @repeats
begin
	insert into dbo.UserCosts(userid,startdate,enddate,workrate) values (@userid, dateadd(day,@i,@start), dateadd(day,@i,@end), @cost)
	set @i = @i + 1
end

set @lastend = dateadd(day,@i - 1,@end)

return 0
GO
/****** Object:  StoredProcedure [dbo].[FAddNode]    Script Date: 28/07/2023 15:49:41 ******/
SET ANSI_NULLS OFF
GO
SET QUOTED_IDENTIFIER OFF
GO
CREATE PROCEDURE [dbo].[FAddNode] (@guid uniqueidentifier, @id bigint, @newguid uniqueidentifier output) AS

-- add specified node to working table, and optionally generate a GUID. Always returns guid, even if specified by user

if @guid is null
	set @guid = newid()

insert into dbo.WNodeStore values (@guid,@id)

set @newguid = @guid

return 0
GO
/****** Object:  StoredProcedure [dbo].[FAddWeekCost]    Script Date: 28/07/2023 15:49:41 ******/
SET ANSI_NULLS OFF
GO
SET QUOTED_IDENTIFIER OFF
GO
CREATE PROCEDURE [dbo].[FAddWeekCost] (@userid int, @startdate datetime, @starttime float, @lunch float, @backlunch float, @hometime float, @cost money, @weeks int, @lastend datetime output) AS

-- Add costs for multiple weeks, assuming two working sessions per day (morning, afternoon). 5 days a week.

declare @d datetime
declare @e datetime
declare @i int


set @i = 0
while @i < @weeks
begin
	-- add mornings
	set @d = dateadd(minute,@starttime * 60,@startdate)
	set @e = dateadd(minute,@lunch * 60,@startdate)
	exec dbo.faddcosts @userid, @d, @e, @cost, 5, @lastend output

	-- add afternoons
	set @d = dateadd(minute,@backlunch * 60,@startdate)
	set @e = dateadd(minute,@hometime * 60,@startdate)
	exec dbo.faddcosts @userid, @d, @e, @cost, 5, @lastend output

	-- move forward a week
	set @i = @i + 1
	set @startdate = dateadd(day,7,@startdate)
end

return 0
GO
/****** Object:  StoredProcedure [dbo].[FChangeSetting]    Script Date: 28/07/2023 15:49:41 ******/
SET ANSI_NULLS OFF
GO
SET QUOTED_IDENTIFIER OFF
GO

CREATE PROCEDURE [dbo].[FChangeSetting] (@userid int, @setting varchar(20), @val varchar(200)) AS

update dbo.UserSettings set val = @val where userid = @userid and setting = @setting

if @@rowcount = 0
begin
	insert into dbo.UserSettings(userid,setting,val) values (@userid,@setting,@val)
end

return 0
GO
/****** Object:  StoredProcedure [dbo].[FLogAction]    Script Date: 28/07/2023 15:49:41 ******/
SET ANSI_NULLS OFF
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE PROCEDURE [dbo].[FLogAction] (@node bigint, @who int, @action varchar(1)) AS

declare @par varchar(200)

if @action is null
	set @action = 'E'

select @par = left(j.description,200) from dbo.JobNodes j where j.idcode = (select p.parentcode from dbo.JobNodes p where p.idcode = @node)

insert into dbo.LogJobs(whoaction, actioncode, idcode, parentcode, parentdesc, description, writeup, priority, needstart, needfinish, substnode, workident, workby, whenstarted, whenfinished,
                      demandach, partialcrit, fullcrit, achieved, contsibling)
	select @who,@action, j.idcode, parentcode, @par, description,writeup,priority,needstart,needfinish,substnode,identval,workby,whenstarted,whenfinished,demandach,partialcrit,fullcrit,achieved,contsibling
		from dbo.JobNodes j left join dbo.WorkRecs w on j.idcode = w.idcode where j.idcode = @node

return 0
GO
/****** Object:  StoredProcedure [dbo].[FLogDelete]    Script Date: 28/07/2023 15:49:41 ******/
SET ANSI_NULLS OFF
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE PROCEDURE [dbo].[FLogDelete] (@node bigint, @who int) AS

insert into LogJobs(whoaction, actioncode, idcode) values (@who,'D',@node);
GO
/****** Object:  StoredProcedure [dbo].[FRegenAch]    Script Date: 28/07/2023 15:49:41 ******/
SET ANSI_NULLS OFF
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE PROCEDURE [dbo].[FRegenAch] AS

begin transaction

delete from dbo.NodeAchs
insert into dbo.NodeAchs
	select idcode, dbo.IsNodeAch(idcode,1), dbo.IsNodeAch2(idcode,1) from dbo.JobNodes

commit

return 0
GO
/****** Object:  StoredProcedure [dbo].[FRemoveOldViewStates]    Script Date: 28/07/2023 15:49:41 ******/
SET ANSI_NULLS OFF
GO
SET QUOTED_IDENTIFIER OFF
GO

CREATE PROCEDURE [dbo].[FRemoveOldViewStates] AS

declare @timeout datetime
set @timeout = dateadd(Hour,-2,getutcdate())
delete from dbo.ViewStates where whenaltered < @timeout

return 0
GO
/****** Object:  StoredProcedure [dbo].[FSplitNode]    Script Date: 28/07/2023 15:49:41 ******/
SET ANSI_NULLS OFF
GO
SET QUOTED_IDENTIFIER OFF
GO

CREATE procedure [dbo].[FSplitNode] (@node bigint, @donetitle varchar(1000), @newtitle varchar(1000), @writeup varchar(max), @postedby int, @workby int, @donenode bigint output, @newnode bigint output, @split bit output)
as
declare @v varchar(1)
declare @when datetime
declare @b bit
declare @id bigint
declare @partial varchar(300)
declare @full varchar(300)
declare @prior int


begin transaction

select @b = contsibling, @partial = partialcrit, @full = fullcrit from dbo.WorkRecs where idcode = @node
if @b is null
begin
	raiserror('Work record does not exist',16,1)
	rollback transaction
	return 1
end

if @workby is null
	set @workby = @postedby

if @b = 1
begin
	-- contsibling is true, but is this valid??
	select @id = parentcode from dbo.JobNodes where idcode = @node
	if @id is null
	begin
		-- no parent!! split instead
		set @id = -1
		set @b = 0
	end
end else
	set @id = -1

if @b = 1
begin
	-- Splitting this job requires sibling node be created. idcode in JobNodes table -> another child of parentcode
	set @donenode = @node

	-- Touch original node to lock it
	update dbo.WorkRecs set idcode = @node where idcode = @node

	-- Add new node for work item
	insert into dbo.JobNodes(parentcode,postedby,description,forwho) values
		(@id,@postedby,@newtitle,@workby)
	set @newnode = SCOPE_IDENTITY()

	-- Add new workrec
	insert into dbo.WorkRecs(idcode,workby,whenstarted,demandach,achieved,contsibling,partialcrit,fullcrit) values
		(@newnode,@workby,GetDate(),'F','W',1, @partial, @full)

	set @split = 0
	exec dbo.FAddToTreeStore @newnode,@id
	exec dbo.FUpdateAchs @newnode,0,0
	exec dbo.FLogAction @newnode,@postedby,'S'
end else
begin
	-- Splitting this job requires 2 child nodes of @node

	select @prior = priority from dbo.JobNodes where idcode = @node
	if @prior is null
		set @prior = 3

	update dbo.JobNodes set description = 'SPLIT: ' + description, inuse = 0 where idcode = @node

	-- Create new node as child of @node
	insert into dbo.JobNodes(parentcode,postedby,description,writeup,forwho,priority) values
		(@node,@postedby,@donetitle,@writeup,@workby,@prior)
	set @donenode = SCOPE_IDENTITY()

	-- Move current work record to new node, and set contsibling=1
	update dbo.WorkRecs set idcode = @donenode, contsibling = 1 where idcode = @node

	-- Create new node as child of @node
	insert into dbo.JobNodes(parentcode,postedby,description,forwho) values
		(@node,@postedby,@newtitle,@workby)
	set @newnode = SCOPE_IDENTITY()

	-- Create new work record
	insert into dbo.WorkRecs(idcode,workby,whenstarted,demandach,achieved,contsibling,partialcrit,fullcrit) values
		(@newnode,@workby,GetDate(),'F','W',1, @partial, @full)

	set @split = 1
	exec dbo.FAddToTreeStore @donenode,@node
	exec dbo.FAddToTreeStore @newnode,@node

	exec dbo.FUpdateAchs @donenode,1,0		-- just do left hand node, not up tree
	exec dbo.FUpdateAchs @newnode,0,0		-- now do new node, and up tree (which includes all parents of @donenode!!)

	exec dbo.FLogAction @node,@postedby,'E'
	exec dbo.FLogAction @donenode,@postedby,'S'
	exec dbo.FLogAction @newnode,@postedby,'S'
end

commit
return 0
GO
/****** Object:  StoredProcedure [dbo].[GetJobWork]    Script Date: 28/07/2023 15:49:41 ******/
SET ANSI_NULLS OFF
GO
SET QUOTED_IDENTIFIER OFF
GO
CREATE PROCEDURE [dbo].[GetJobWork] (@idcode bigint) AS

declare @who int
declare @whenstart datetime
declare @whenend datetime
declare @stat varchar(1)
declare @i int

-- Return work and cost data for a specific work record
select @stat = achieved, @who = workby, @whenstart = whenstarted, @whenend = whenfinished from dbo.WorkRecs where idcode = @idcode
if @who is null
begin
	raiserror('This work record is not assigned to a user, or is not a Leaf',16,1)
	return 1
end
if @stat = 'W'
begin
	-- waiting job, so no progress
	return 0
end
if @whenend is null
	set @whenend = GetDate()

exec @i = dbo.GetWorkData @whenstart, @whenend, @who
return @i
GO
/****** Object:  StoredProcedure [dbo].[GetSettings]    Script Date: 28/07/2023 15:49:41 ******/
SET ANSI_NULLS OFF
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE PROCEDURE [dbo].[GetSettings] (@userid int) AS

select setting,val from dbo.UserSettings where userid = @userid
return 0
GO
/****** Object:  StoredProcedure [dbo].[GetViewState]    Script Date: 28/07/2023 15:49:41 ******/
SET ANSI_NULLS OFF
GO
SET QUOTED_IDENTIFIER OFF
GO


CREATE procedure [dbo].[GetViewState](@hash varchar(50)) as

select viewstate from dbo.ViewStates where hash = @hash
if @@rowcount = 0
begin
	raiserror('Could not find viewstate in db',16,1)
	return 1
end

return 0
GO
/****** Object:  StoredProcedure [dbo].[VNextNodes]    Script Date: 28/07/2023 15:49:41 ******/
SET ANSI_NULLS OFF
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE PROCEDURE [dbo].[VNextNodes] (@node bigint) AS


SELECT     JobsRequired.requiredfor, left(JobNodes.description,100) as description, JobNodes.needstart, JobNodes.needfinish, ach2 AS Ach, dbo.CheckReqNodes(requiredfor) as NeededJob, w.idcode as LeafCode
FROM         dbo.JobsRequired INNER JOIN
                      dbo.JobNodes ON JobsRequired.requiredfor = JobNodes.idcode INNER JOIN
	         dbo.NodeAchs n on JobNodes.idcode = n.idcode left join dbo.WorkRecs w on JobNodes.idcode = w.idcode
WHERE     (JobsRequired.jobrequired = @node)
order by priority,isnull(needfinish,'1/1/2100'),requiredfor

return 0
GO
/****** Object:  StoredProcedure [dbo].[VReqNodes]    Script Date: 28/07/2023 15:49:41 ******/
SET ANSI_NULLS OFF
GO
SET QUOTED_IDENTIFIER OFF
GO
CREATE PROCEDURE [dbo].[VReqNodes] (@node bigint, @any int, @req bigint output, @reqdesc varchar(1000) output) AS

-- Does not check for dependances of parent node(s). Use CheckReqNodes() UDF instead

SELECT     JobsRequired.jobrequired, left(JobNodes.description,100) as description, JobNodes.needfinish, ach2 AS Ach, w.idcode as LeafCode
FROM         dbo.JobsRequired INNER JOIN
                      dbo.JobNodes ON JobsRequired.jobrequired = JobNodes.idcode INNER JOIN
	         dbo.NodeAchs n on JobNodes.idcode = n.idcode left join dbo.WorkRecs w on JobNodes.idcode = w.idcode
WHERE     (JobsRequired.requiredfor = @node)
order by priority,isnull(needfinish,'1/1/2100'),jobrequired

if @any = 1
begin
	set @req = dbo.CheckReqNodes(@node)
	if @req is not null
		select @reqdesc = description from dbo.JobNodes where idcode = @req
end

return 0
GO
/****** Object:  StoredProcedure [dbo].[VReqNodesSimple]    Script Date: 28/07/2023 15:49:41 ******/
SET ANSI_NULLS OFF
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE PROCEDURE [dbo].[VReqNodesSimple] (@node bigint) AS

declare @b bigint
declare @v varchar(10)

-- call VReqNodes with a simplified interface
exec dbo.VReqNodes @node, 0, @b, @v
GO
/****** Object:  StoredProcedure [dbo].[VWorkPlan]    Script Date: 28/07/2023 15:49:41 ******/
SET ANSI_NULLS OFF
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE PROCEDURE [dbo].[VWorkPlan] (@excludedone bit, @fornode bigint) AS

if @fornode = -1
	set @fornode = null

SELECT     GetReqDepends.node, GetReqDepends.lvl, LEFT(JobNodes.description, 200) AS descr, isnull(left(par.description,200),'(no parent)') as parentdesc, isnull(JobNodes.parentcode,-1) parentcode, ach2 AS IsAch, WorkRecs.achieved,
                      JobNodes.needstart AS needstart, JobNodes.needfinish AS needfinish, WorkRecs.workby, Users.Fullname as WorkName, GetReqDepends.status
FROM         dbo.GetReqDepends(@excludedone,@fornode) GetReqDepends INNER JOIN
                      dbo.WorkRecs ON GetReqDepends.node = WorkRecs.idcode INNER JOIN
                      dbo.JobNodes ON WorkRecs.idcode = JobNodes.idcode inner join
	         dbo.NodeAchs n on JobNodes.idcode = n.idcode left join
	         dbo.Users on WorkRecs.workby = Users.userid left join
	         dbo.JobNodes par on JobNodes.parentcode = par.idcode
ORDER BY GetReqDepends.lvl, ISNULL(JobNodes.needfinish, '1/1/2100'), WorkRecs.idcode
GO
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'PK of parent node, or null if root' , @level0type=N'SCHEMA',@level0name=N'dbo', @level1type=N'TABLE',@level1name=N'JobNodes', @level2type=N'COLUMN',@level2name=N'parentcode'
GO
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'Description of this job / project' , @level0type=N'SCHEMA',@level0name=N'dbo', @level1type=N'TABLE',@level1name=N'JobNodes', @level2type=N'COLUMN',@level2name=N'description'
GO
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'Duration in hours expected to take (working hours)' , @level0type=N'SCHEMA',@level0name=N'dbo', @level1type=N'TABLE',@level1name=N'JobNodes', @level2type=N'COLUMN',@level2name=N'expecteddur'
GO
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'Expected cost of job' , @level0type=N'SCHEMA',@level0name=N'dbo', @level1type=N'TABLE',@level1name=N'JobNodes', @level2type=N'COLUMN',@level2name=N'expectedcost'
GO
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'Needs to start by this date' , @level0type=N'SCHEMA',@level0name=N'dbo', @level1type=N'TABLE',@level1name=N'JobNodes', @level2type=N'COLUMN',@level2name=N'needstart'
GO
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'Needs to be completed by this date' , @level0type=N'SCHEMA',@level0name=N'dbo', @level1type=N'TABLE',@level1name=N'JobNodes', @level2type=N'COLUMN',@level2name=N'needfinish'
GO
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'Who is to work on job. Optional' , @level0type=N'SCHEMA',@level0name=N'dbo', @level1type=N'TABLE',@level1name=N'JobNodes', @level2type=N'COLUMN',@level2name=N'forwho'
GO
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'Description of results of work' , @level0type=N'SCHEMA',@level0name=N'dbo', @level1type=N'TABLE',@level1name=N'JobNodes', @level2type=N'COLUMN',@level2name=N'writeup'
GO
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'Points to another node who''s status replaces this one' , @level0type=N'SCHEMA',@level0name=N'dbo', @level1type=N'TABLE',@level1name=N'JobNodes', @level2type=N'COLUMN',@level2name=N'substnode'
GO
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'Priority of job' , @level0type=N'SCHEMA',@level0name=N'dbo', @level1type=N'TABLE',@level1name=N'JobNodes', @level2type=N'COLUMN',@level2name=N'priority'
GO
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'Job that this records demands is started / finished before new job in started' , @level0type=N'SCHEMA',@level0name=N'dbo', @level1type=N'TABLE',@level1name=N'JobsRequired', @level2type=N'COLUMN',@level2name=N'jobrequired'
GO
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'Job that can be started when specified job is started / finished' , @level0type=N'SCHEMA',@level0name=N'dbo', @level1type=N'TABLE',@level1name=N'JobsRequired', @level2type=N'COLUMN',@level2name=N'requiredfor'
GO
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'Special hourly rate for this node and this person' , @level0type=N'SCHEMA',@level0name=N'dbo', @level1type=N'TABLE',@level1name=N'NodeRates', @level2type=N'COLUMN',@level2name=N'rateoverride'
GO
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'Where this user starts from' , @level0type=N'SCHEMA',@level0name=N'dbo', @level1type=N'TABLE',@level1name=N'Users', @level2type=N'COLUMN',@level2name=N'homenode'
GO
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'Can this user view costs' , @level0type=N'SCHEMA',@level0name=N'dbo', @level1type=N'TABLE',@level1name=N'Users', @level2type=N'COLUMN',@level2name=N'viewcosts'
GO
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'Node this item refers to' , @level0type=N'SCHEMA',@level0name=N'dbo', @level1type=N'TABLE',@level1name=N'WorkRecs', @level2type=N'COLUMN',@level2name=N'idcode'
GO
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'Who did this work' , @level0type=N'SCHEMA',@level0name=N'dbo', @level1type=N'TABLE',@level1name=N'WorkRecs', @level2type=N'COLUMN',@level2name=N'workby'
GO
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'What level of success constitutes achievement?' , @level0type=N'SCHEMA',@level0name=N'dbo', @level1type=N'TABLE',@level1name=N'WorkRecs', @level2type=N'COLUMN',@level2name=N'demandach'
GO
EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'Continuation of this work will be sibling, not child node' , @level0type=N'SCHEMA',@level0name=N'dbo', @level1type=N'TABLE',@level1name=N'WorkRecs', @level2type=N'COLUMN',@level2name=N'contsibling'
GO
USE [master]
GO
ALTER DATABASE [JobTrack] SET  READ_WRITE
GO
