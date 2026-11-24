-- Session version: incremented on sign-out and password change; older session cookies are refused.
ALTER TABLE admin ADD COLUMN session_version bigint NOT NULL DEFAULT 1;
