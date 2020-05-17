#!/bin/bash

# local build, test and package
dotnet build --configuration Release && \
    dotnet test --configuration Release --no-build && \
    dotnet pack --configuration Release --no-build --output artifacts src/PrioritySchedulingTools
