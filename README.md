# Introduction

This project hosts a server with a front end which can be used to build BTCPay Server plugins and store the binaries on some storage.

## Prerequisite:

It assumes you installed docker on your system.

## Configuration

All parameters are configured via environment variables.

* `PB_POSTGRES`: Connection to a postgres database (example: `User ID=postgres;Include Error Detail=true;Host=127.0.0.1;Port=61932;Database=blah`)