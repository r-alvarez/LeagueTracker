CREATE TABLE "Accounts" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_Accounts" PRIMARY KEY,
    "Puuid" TEXT NULL,
    "OwnerUserId" TEXT NULL,
    "MediaPublic" INTEGER NOT NULL,
    "PreviousSlugs" TEXT NOT NULL,
    "CreatedUtc" TEXT NOT NULL,
    "Slug" TEXT NOT NULL,
    "GameName" TEXT NOT NULL,
    "TagLine" TEXT NOT NULL,
    "Region" TEXT NOT NULL,
    "Platform" TEXT NOT NULL,
    "DataDir" TEXT NOT NULL,
    "Hosts" TEXT NOT NULL,
    "DisplayName" TEXT NOT NULL,
    "HideLp" INTEGER NOT NULL,
    "FromConfig" INTEGER NOT NULL
);
CREATE TABLE "AgentKeys" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_AgentKeys" PRIMARY KEY,
    "Name" TEXT NOT NULL,
    "Machine" TEXT NOT NULL,
    "KeyHash" TEXT NOT NULL,
    "Status" TEXT NOT NULL,
    "CreatedUtc" TEXT NOT NULL,
    "DecidedUtc" TEXT NULL,
    "LastSeenUtc" TEXT NULL,
    "LastIp" TEXT NULL,
    "Note" TEXT NULL,
    "OwnerUserId" TEXT NULL,
    "Role" TEXT NOT NULL
, ActsForAccountIds TEXT NULL);
CREATE TABLE "JoinCodes" (
    "Code" TEXT NOT NULL CONSTRAINT "PK_JoinCodes" PRIMARY KEY,
    "OwnerUserId" TEXT NOT NULL,
    "Role" TEXT NOT NULL,
    "CreatedUtc" TEXT NOT NULL,
    "ExpiresUtc" TEXT NOT NULL,
    "UsedUtc" TEXT NULL,
    "UsedByKeyId" TEXT NULL
);
CREATE TABLE "OwnershipClaims" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_OwnershipClaims" PRIMARY KEY,
    "AccountId" TEXT NOT NULL,
    "UserId" TEXT NOT NULL,
    "IconId" INTEGER NOT NULL,
    "CreatedUtc" TEXT NOT NULL,
    "ExpiresUtc" TEXT NOT NULL,
    "Attempts" INTEGER NOT NULL,
    "State" TEXT NOT NULL
);
CREATE TABLE "UserLogins" (
    "Id" INTEGER NOT NULL CONSTRAINT "PK_UserLogins" PRIMARY KEY AUTOINCREMENT,
    "UserId" TEXT NOT NULL,
    "Issuer" TEXT NOT NULL,
    "Subject" TEXT NOT NULL,
    "CreatedUtc" TEXT NOT NULL,
    "LastUsedUtc" TEXT NULL,
    CONSTRAINT "FK_UserLogins_Users_UserId" FOREIGN KEY ("UserId") REFERENCES "Users" ("Id") ON DELETE CASCADE
);
CREATE TABLE "Users" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_Users" PRIMARY KEY,
    "Email" TEXT NOT NULL,
    "DisplayName" TEXT NOT NULL,
    "IsAdmin" INTEGER NOT NULL,
    "CreatedUtc" TEXT NOT NULL,
    "LastSeenUtc" TEXT NULL
, InvitedUtc TEXT NULL, InvitedByUserId TEXT NULL, InviteSentUtc TEXT NULL, ProviderUserId TEXT NULL);
CREATE INDEX "IX_Accounts_OwnerUserId" ON "Accounts" ("OwnerUserId");
CREATE UNIQUE INDEX "IX_Accounts_Puuid" ON "Accounts" ("Puuid");
CREATE UNIQUE INDEX "IX_Accounts_Slug" ON "Accounts" ("Slug");
CREATE UNIQUE INDEX "IX_AgentKeys_KeyHash" ON "AgentKeys" ("KeyHash");
CREATE INDEX "IX_OwnershipClaims_AccountId" ON "OwnershipClaims" ("AccountId");
CREATE UNIQUE INDEX "IX_UserLogins_Issuer_Subject" ON "UserLogins" ("Issuer", "Subject");
CREATE INDEX "IX_UserLogins_UserId" ON "UserLogins" ("UserId");
CREATE UNIQUE INDEX "IX_Users_Email" ON "Users" ("Email");
