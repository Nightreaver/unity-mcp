---
title: manage_asset_store
sidebar_label: manage_asset_store
description: "Manage Unity Asset Store packages: list purchases, download, and import."
---

# `manage_asset_store`

> **Auto-generated** from the Python tool registry. Do not hand-edit outside `<!-- examples:start --><!-- examples:end -->` blocks — the generator (`tools/generate_docs_reference.py`) will overwrite them.

**Group:** `core` &nbsp;·&nbsp; **Module:** `services.tools.manage_asset_store`

## Description

Manage Unity Asset Store packages: list purchases, download, and import.

AUTH:
- check_auth: Check if user is logged into their Unity account

QUERY:
- list_purchases: List purchased Asset Store packages (My Assets)

INSTALL:
- download: Download an Asset Store package by product ID
- import: Import an already-downloaded Asset Store package

## Parameters

| Name | Type | Required | Description |
|------|------|----------|-------------|
| `action` | `str` | yes | The asset store action to perform. |
| `product_id` | `int \| None` | — | Asset Store product ID (for download/import actions). |
| `page` | `int \| None` | — | Page number for list_purchases (1-based). |
| `page_size` | `int \| None` | — | Results per page for list_purchases. |

## Returns

A `dict` containing the Unity response. The exact shape depends on the action.

## Examples

<!-- examples:start -->
*No examples yet. Add usage examples here — they will be preserved across regenerations.*
<!-- examples:end -->

